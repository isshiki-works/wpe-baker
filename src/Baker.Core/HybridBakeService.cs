using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baker.Core;

public sealed record HybridBakeRequest(int SchemaVersion, JsonObject Plan, string OutputDirectory,
    ulong ProbeFrames = 0, string? DeviceUuid = null, string? ProjectDirectory = null,
    // 播放版编码档位：software（默认）/ nvenc / qsv / amf / auto。无损 master 不受影响。
    string? PlaybackEncoder = null,
    // 成品编码的跨进程槽位配额：0 = 不限。多槽并行跑批时用它压住 ffmpeg 抢核，渲染不受限制。
    int EncodeSlots = 0,
    // 单案内同时在飞的组主渲染数：1 = 与逐组串行完全一致。组的判定、编码与写入始终按组序串行。
    int GroupParallel = 1,
    // 开发用：保留中间产物（capture-source、各组 master、合成探针与参照、分析刷新目录）。
    // 默认 false —— 正常结束、拒绝与失败都会删掉它们，只留成品工程、bake.json、接缝预览与日志。
    bool KeepIntermediates = false);

/// <summary>Replaces rendered scene groups with videos and retains the original live hierarchies.</summary>
/// <summary>1 帧、0 个视频层的结果：烘完等于一张静态图加全部实时，不省电，所以不算成品（fix/verdict-flow）。</summary>
public static class StaticOnlyBake
{
    public const string Status = "static_only";

    /// <summary>这份烘焙结果是不是"只剩一张静态图"：帧数 ≤ 1 且一个视频层都没有。</summary>
    public static bool Is(JsonObject report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return Count(report["video_layers"]) == 0 && Count(report["frames"]) is double frames && frames <= 1;
    }

    /// <summary>报告里的计数：int / long / ulong / JSON 数字都按同一条路读，读不到就是 null。</summary>
    private static double? Count(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue(out double number) && double.IsFinite(number)) return number;
        if (value.TryGetValue(out long integer)) return integer;
        if (value.TryGetValue(out ulong unsigned)) return unsigned;
        if (value.TryGetValue(out int small)) return small;
        return null;
    }

    /// <summary>成品与"只剩一张静态图"都算烘完了：工程照样写出去，只是结论分开说。</summary>
    public static bool Finished(string? status) => status is "candidate_generated" or Status;
}

public sealed class HybridBakeService(NativeTools tools)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static JsonArray? SourceScriptErrors(JsonObject evidence)
    {
        if (evidence["source_script_error_count"] is JsonValue count && count.TryGetValue<int>(out int errorCount) &&
            errorCount >= 0 && evidence["source_script_errors"] is JsonArray errors && errors.Count == errorCount &&
            errors.All(error => error is JsonObject)) return errors;
        return null;
    }

    /// <summary>
    /// 这个视频组的成品能不能在渲染那一遍直接编出来，不写无损 master。三个条件都是必要的：
    /// <list type="bullet">
    /// <item>不透明（<c>include_scene_clear</c>）：裁剪范围先验就是整幅，编码参数在渲染前已知。透明组的裁剪区是全片
    /// alpha 并集，渲完才知道，必须先落 master。</item>
    /// <item>不是残差组：残差组的成品是"淡化改写后的 master 再整片编一遍"，任何分段编码都会改变 x264 状态。</item>
    /// <item>不是探针：探针只量字节，走原路线更省事，也不必为它引入第二条编码链。</item>
    /// </list>
    /// </summary>
    public static bool AllowsDirectPlayback(bool includeSceneClear, bool residualGroup, ulong probeFrames) =>
        includeSceneClear && !residualGroup && probeFrames == 0;

    /// <summary>
    /// "这一案还没跑完"的状态。硬超时或硬杀之后留在 bake.json 里的就是它：候选回退与分配回退都要跑几十分钟，
    /// 期间磁盘上不能留着上一段的结论（那会被下游当成最终结果，把整轮工作量记成上一段的零点几秒）。
    /// 不在界面的"已完成"白名单里，也不是任何拒绝状态。
    /// </summary>
    public const string InProgressStatus = "in_progress";

    /// <summary>
    /// 把报告标成"还在跑"，记下这是第几个候选与处在哪一段回退。正常结束时再由各自的出口写最终状态。
    /// </summary>
    internal static void MarkInProgress(JsonObject report, int candidateAttempt, string stage)
    {
        ArgumentNullException.ThrowIfNull(report);
        report["status"] = InProgressStatus;
        report["in_progress_stage"] = stage;
        report["candidate_attempt"] = candidateAttempt;
        report["loop_validation"] = "not_performed";
        report.Remove("reason");
        report.Remove("reason_localized");
    }

    private static JsonArray? PlannedSourceScriptErrors(JsonObject plan) =>
        plan["source_script_error_evidence"]?["status"]?.GetValue<string>() == "available" ? SourceScriptErrors(plan) : null;

    private static void AttachPlanProvenance(JsonObject report, JsonObject plan)
    {
        if (PlannedSourceScriptErrors(plan) is JsonArray errors)
        {
            report["source_script_error_count"] = plan["source_script_error_count"]!.DeepClone();
            report["source_script_errors"] = errors.DeepClone();
        }
        else report["source_script_error_evidence"] = "not_available";
        // 事后核对用：成品原样带上分析设备，旧计划没有这一项时不补造。
        if (plan["analysis_device"] is JsonNode device) report["analysis_device"] = device.DeepClone();
    }

    internal static JsonObject ClassifyLateSourceScriptErrors(JsonArray errors, IReadOnlySet<int> groupLayers,
        IReadOnlySet<int> liveLayerIds, IReadOnlyDictionary<int, JsonObject> sourceObjects)
        => HybridExportSafety.ClassifyLateSourceScriptErrors(errors, groupLayers, liveLayerIds, sourceObjects);

    // 非实时对象保留下来时要去掉的绘制相关键。
    private static readonly string[] NonLiveDrawKeys = ["image", "model", "text", "particle", "sound", "effects", "puppet"];

    private static bool HasScriptCode(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out string? code) && !string.IsNullOrWhiteSpace(code);

    /// <summary>
    /// 去掉绘制键之后，对象自身或它的某个属性绑定上还带 script 字段。
    /// 只看对象这一层和它的直接属性，不按壁纸或脚本内容做特判。
    /// </summary>
    internal static bool CarriesRetainableScript(JsonObject obj) =>
        HasScriptCode(obj["script"]) ||
        obj.Where(property => !NonLiveDrawKeys.Contains(property.Key))
            .Any(property => property.Value is JsonObject binding && HasScriptCode(binding["script"]));

    /// <summary>
    /// 原作里"不绘制但带脚本"的根对象：计划里不是实时、不在视频组、没被省略或排除，也不绘制，
    /// 整棵子树里没有实时层、视频层或视频组的挂载父级（那些情况已经由父级保留路径按原位置输出）。
    /// 它们的脚本可能经 shared 之类的全局对象给保留的实时脚本提供函数，查找记录抓不到这种依赖，
    /// 所以按源顺序原样保留（去掉绘制键）。
    /// </summary>
    internal static int[] ScriptRootIds(IReadOnlyDictionary<int, JsonObject> originalObjects, JsonObject plan)
    {
        var layerInfo = plan["layers"]!.AsArray().OfType<JsonObject>().ToDictionary(HybridScenePlanner.Id);
        var liveIds = plan["live_layer_ids"]!.AsArray().Select(n => n!.GetValue<int>()).ToHashSet();
        var omitted = (plan["omitted_snapshot_layer_ids"] as JsonArray ?? []).Select(n => n!.GetValue<int>()).ToHashSet();
        var excluded = (plan["excluded_layer_ids"] as JsonArray ?? []).Select(n => HybridScenePlanner.Int(n)).OfType<int>().ToHashSet();
        var groups = (plan["video_groups"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var videoIds = groups.SelectMany(group => (group["layer_ids"] as JsonArray ?? []).Select(n => HybridScenePlanner.Int(n)).OfType<int>()).ToHashSet();
        var groupParents = groups.Select(group => HybridScenePlanner.Int(group["parent_id"])).OfType<int>().ToHashSet();
        bool IsRoot(JsonObject obj) => HybridScenePlanner.Int(obj["parent"]) is not int parent || !originalObjects.ContainsKey(parent);
        bool SubtreeRetainedElsewhere(int id) =>
            liveIds.Contains(id) || videoIds.Contains(id) || groupParents.Contains(id) ||
            originalObjects.Where(pair => HybridScenePlanner.Int(pair.Value["parent"]) == id).Any(pair => SubtreeRetainedElsewhere(pair.Key));
        var roots = new List<int>();
        foreach (var (id, obj) in originalObjects)
        {
            if (!IsRoot(obj) || !layerInfo.TryGetValue(id, out var layer)) continue;
            if (omitted.Contains(id) || excluded.Contains(id)) continue;
            if (layer["allocation"] is JsonValue allocation && allocation.GetValue<string>() != "inactive") continue;
            if (layer["drawable"] is not JsonValue drawable || !drawable.TryGetValue(out bool draws) || draws) continue;
            if (CarriesRetainableScript(obj) && !SubtreeRetainedElsewhere(id)) roots.Add(id);
        }
        return roots.ToArray();
    }

    internal static JsonArray AssembleObjects(IReadOnlyDictionary<int, JsonObject> originalObjects, JsonObject plan,
        IReadOnlyDictionary<string, JsonObject> replacements, JsonArray dependencies)
    {
        var layerInfo = plan["layers"]!.AsArray().OfType<JsonObject>().ToDictionary(HybridScenePlanner.Id);
        var liveIds = plan["live_layer_ids"]!.AsArray().Select(n => n!.GetValue<int>()).ToHashSet();
        var omitted = (plan["omitted_snapshot_layer_ids"] as JsonArray ?? []).Select(n => n!.GetValue<int>()).ToHashSet();
        var finalObjects = new JsonArray();
        var emitted = new HashSet<int>();
        var expected = new List<int>();
        var sourceDrawOrder = new List<int>();
        void SourceOrder(int id)
        {
            sourceDrawOrder.Add(id);
            foreach (var (child, obj) in originalObjects.Where(pair => HybridScenePlanner.Int(pair.Value["parent"]) == id)) SourceOrder(child);
        }
        foreach (var (id, obj) in originalObjects.Where(pair => HybridScenePlanner.Int(pair.Value["parent"]) is not int parent || !originalObjects.ContainsKey(parent))) SourceOrder(id);
        void Emit(int id)
        {
            if (!originalObjects.TryGetValue(id, out var original) || !emitted.Add(id)) return;
            if (omitted.Contains(id)) throw new InvalidDataException("A retained object depends on an omitted snapshot ancestor or identity.");
            if (HybridScenePlanner.Int(original["parent"]) is int parent && originalObjects.ContainsKey(parent)) Emit(parent);
            var obj = original.DeepClone().AsObject();
            if (!liveIds.Contains(id))
                foreach (string key in NonLiveDrawKeys) obj.Remove(key);
            finalObjects.Add(obj);
        }
        // 不绘制但带脚本的根对象按源顺序放在最前：保证它们的 init 先于保留的实时脚本执行。
        // 它们不绘制，不进 expected，不影响下面的视频/实时绘制顺序校验。
        foreach (int id in ScriptRootIds(originalObjects, plan)) Emit(id);
        foreach (var entry in plan["composition"]!.AsArray().OfType<JsonObject>())
        {
            if (entry["video_group"] is JsonValue groupName && replacements.TryGetValue(groupName.GetValue<string>(), out var replacement))
            {
                var group = plan["video_groups"]!.AsArray().OfType<JsonObject>().Single(g => g["id"]!.GetValue<string>() == groupName.GetValue<string>());
                if (HybridScenePlanner.Int(replacement["parent"]) != HybridScenePlanner.Int(group["parent_id"]))
                    throw new InvalidDataException("A video replacement must use its planned source sibling parent.");
                if (HybridScenePlanner.Int(replacement["parent"]) is int parent) Emit(parent);
                finalObjects.Add(replacement.DeepClone());
                expected.Add(HybridScenePlanner.Id(replacement));
            }
            else if (HybridScenePlanner.Int(entry["live_root"]) is int unit)
                foreach (int id in sourceDrawOrder)
                    if (liveIds.Contains(id) && HybridScenePlanner.Int(layerInfo[id]["allocation_root"] ?? layerInfo[id]["root"]) == unit)
                    {
                        Emit(id);
                        if (layerInfo[id]["drawable"]?.GetValue<bool>() == true) expected.Add(id);
                    }
        }
        foreach (var dependency in dependencies.OfType<JsonObject>())
            if (HybridScenePlanner.Int(dependency["owner"]) is int owner && liveIds.Contains(owner) &&
                HybridScenePlanner.Int(dependency["target"]) is int target && originalObjects.ContainsKey(target)) Emit(target);

        // Native FinalizeScene appends siblings in declaration order; EmitSceneNode traverses them
        // depth first. Verify parented videos occupy the planned sibling positions.
        var all = finalObjects.OfType<JsonObject>().ToArray();
        var ids = all.Select(HybridScenePlanner.Id).ToHashSet();
        var expectedIds = expected.ToHashSet();
        var actual = new List<int>();
        void Visit(int id)
        {
            if (expectedIds.Contains(id)) actual.Add(id);
            foreach (var child in all.Where(obj => HybridScenePlanner.Int(obj["parent"]) == id)) Visit(HybridScenePlanner.Id(child));
        }
        foreach (var obj in all.Where(obj => HybridScenePlanner.Int(obj["parent"]) is not int parent || !ids.Contains(parent)))
            Visit(HybridScenePlanner.Id(obj));
        if (!actual.SequenceEqual(expected))
            throw new InvalidDataException(Messages.Emit("blocker.hierarchy_changes_draw_order"));
        GuardPublicLayerQueries(originalObjects.Values, all, dependencies);
        return finalObjects;
    }

    internal static void GuardPublicLayerQueries(IEnumerable<JsonObject> originalObjects,
        IEnumerable<JsonObject> finalObjects, JsonArray dependencies)
        => HybridExportSafety.GuardPublicLayerQueries(originalObjects, finalObjects, dependencies);

    internal static JsonArray MergeRuntimeDependencies(JsonArray first, JsonArray second)
        => HybridExportSafety.MergeRuntimeDependencies(first, second);

    // Kept for the existing reflection-based Core test; production calls the shared helper directly.
    private static JsonArray LateExternalDependencies(JsonObject master, IReadOnlySet<int> groupLayers,
        IReadOnlyDictionary<int, JsonObject> sourceObjects) =>
        HybridExportSafety.LateExternalDependencies(master, groupLayers, sourceObjects);

    /// <summary>
    /// 一次烘焙的外层入口。中间产物（捕获副本、各组无损 master、合成探针与参照、分析刷新目录）在这里统一清理：
    /// 成功、拒绝、失败、取消都清，只留成品工程、bake.json、接缝预览与日志。
    /// <c>--keep-intermediates</c>（<see cref="HybridBakeRequest.KeepIntermediates"/>）给开发者留原样；
    /// 合成校验被拒时保留探针与参照，报告里的 probe_paths 才有东西可看。
    /// 短探针那次调用不清理：它的产物由外层这次调用连目录一起删。
    /// </summary>
    public async Task<JsonObject> BakeAsync(HybridBakeRequest request, IProgress<RenderProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProbeFrames > 0 || request.KeepIntermediates)
            return await BakeRunAsync(request, progress, cancellationToken);
        string output = Path.GetFullPath(request.OutputDirectory);
        JsonObject? result = null;
        try { return result = await BakeRunAsync(request, progress, cancellationToken); }
        finally
        {
            JsonArray? cleanupErrors = RemoveIntermediates(output, keepCompositionProbe: result?["probe_paths"] is JsonObject);
            if (result is not null)
            {
                result["intermediates_removed"] = true;
                if (cleanupErrors is not null) result["intermediate_cleanup_errors"] = cleanupErrors;
                try { await File.WriteAllTextAsync(Path.Combine(output, "bake.json"), result.ToJsonString(JsonOptions), CancellationToken.None); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>这一案结束后不再需要的目录。合成探针与参照在被拒的报告里还被 probe_paths 引用，按需保留。</summary>
    internal static JsonArray? RemoveIntermediates(string outputDirectory, bool keepCompositionProbe = false)
    {
        JsonArray? errors = null;
        void Remove(string directory)
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                (errors ??= []).Add(new JsonObject { ["path"] = directory, ["error"] = error.Message });
            }
        }
        // 候选回退那一次烘焙的输出在 loop-allocation-candidate 下，中间产物同名同结构。
        foreach (string root in new[] { outputDirectory, Path.Combine(outputDirectory, "loop-allocation-candidate") })
        {
            foreach (string suffix in keepCompositionProbe
                ? new[] { ".analysis-refresh" }
                : [".composition-probe", ".composition-reference", ".analysis-refresh"])
                Remove(root + suffix);
            if (!Directory.Exists(root)) continue;
            Remove(Path.Combine(root, "capture-source"));
            string[] children;
            try { children = Directory.GetDirectories(root); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            foreach (string child in children) Remove(Path.Combine(child, "master"));
        }
        return errors;
    }

    private async Task<JsonObject> BakeRunAsync(HybridBakeRequest request, IProgress<RenderProgress>? progress,
        CancellationToken cancellationToken)
    {
        var timing = new StageTiming();
        timing.SetDevice(request.DeviceUuid ?? request.Plan["settings"]?["device_uuid"]?.GetValue<string>());
        if (request.Plan["route"]?.GetValue<string>() == "effect_prefix")
            return await BakeEffectPrefixesAsync(request, progress, timing, cancellationToken);
        JsonObject initial = await BakeOnceAsync(request, progress, timing, cancellationToken);
        if (request.ProbeFrames > 0 || initial["status"]?.GetValue<string>() is not
            ("candidate_rejected_no_loop" or "candidate_rejected_seam")) return initial;
        var plan = initial["plan"]!.AsObject();
        using var source = new ProjectSource(plan["source"]!.GetValue<string>());
        JsonObject? proposal = HybridLoopAllocation.Propose(plan, source.ReadJson(source.SceneResource));
        if (proposal is null) return initial;

        string output = Path.GetFullPath(request.OutputDirectory);
        string initialPath = Path.Combine(output, "before-loop-allocation.json");
        await VideoSceneBuilder.WriteJsonAsync(initialPath, initial, cancellationToken);
        var evidence = proposal.DeepClone().AsObject();
        evidence["initial_status"] = initial["status"]!.DeepClone();
        evidence["initial_report_path"] = initialPath;
        evidence["status"] = "replanning";
        initial["loop_allocation_fallback"] = evidence;
        string reportPath = Path.Combine(output, "bake.json");
        // 回退要跑几十分钟，期间硬超时或硬杀留在磁盘上的必须是"未完成"，不能是回退前那份
        // candidate_rejected_no_loop（它会被当成最终结论，把 40 分钟的工作量记成 0.31 秒）。
        // 回退前的原状态仍在 evidence.initial_status 与 before-loop-allocation.json 里。
        var replanning = initial.DeepClone().AsObject();
        MarkInProgress(replanning, candidateAttempt: 0, stage: "loop_allocation_replanning");
        await File.WriteAllTextAsync(reportPath, replanning.ToJsonString(JsonOptions), cancellationToken);
        try
        {
            if (await source.SourceHashAsync(cancellationToken) != plan["source_sha256"]!.GetValue<string>())
                throw new IOException("Source changed before loop allocation fallback.");
            var settings = plan["settings"]!.Deserialize<HybridAnalyzeRequest>(JsonOptions)
                ?? throw new InvalidDataException("Invalid loop fallback settings.");
            string analysisOutput = Path.Combine(output, "loop-allocation-analysis");
            evidence["analysis_plan_path"] = Path.Combine(analysisOutput, "plan.json");
            progress?.Report(new("retaining_nonlooping_layers", 0,
                "Keeping unresolved effects and particles live, then checking one smaller bake allocation."));
            JsonObject replanned = await new HybridScenePlanner(tools).AnalyzeSingleAsync(settings with {
                Source = source.SourcePath, OutputDirectory = analysisOutput, RuntimeTraceFile = null,
                RetainLiveRootIds = proposal["retain_live_root_ids"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray()
                }, progress, cancellationToken);
            if (replanned["source_sha256"]!.GetValue<string>() != plan["source_sha256"]!.GetValue<string>())
                throw new IOException("Source changed during loop allocation fallback.");
            if (replanned["blockers"] is JsonArray { Count: > 0 })
            {
                evidence["status"] = "requires_resolution";
                evidence["blockers"] = replanned["blockers"]!.DeepClone();
                timing.Stamp(initial);
                await File.WriteAllTextAsync(reportPath, initial.ToJsonString(JsonOptions), cancellationToken);
                return initial;
            }
            // One retry through all normal composition and loop checks; never recurse the fallback.
            var retry = request with { Plan = replanned, OutputDirectory = Path.Combine(output, "loop-allocation-candidate") };
            JsonObject result = replanned["route"]?.GetValue<string>() == "effect_prefix"
                ? await BakeEffectPrefixesAsync(retry, progress, timing, cancellationToken)
                : await BakeOnceAsync(retry, progress, timing, cancellationToken);
            evidence["status"] = result["status"]!.DeepClone();
            result["loop_allocation_fallback"] = evidence.DeepClone();
            timing.Stamp(result);
            await File.WriteAllTextAsync(reportPath, result.ToJsonString(JsonOptions), cancellationToken);
            if (request.ProjectDirectory is not null && StaticOnlyBake.Finished(result["status"]?.GetValue<string>()))
                await File.WriteAllTextAsync(Path.Combine(request.ProjectDirectory, "bake.json"), result.ToJsonString(JsonOptions), cancellationToken);
            return result;
        }
        catch (Exception error)
        {
            initial["status"] = cancellationToken.IsCancellationRequested ? "cancelled" : "failed";
            evidence["status"] = initial["status"]!.DeepClone();
            evidence["error_type"] = error.GetType().Name;
            evidence["error"] = error.Message;
            timing.Stamp(initial);
            await File.WriteAllTextAsync(reportPath, initial.ToJsonString(JsonOptions), CancellationToken.None);
            throw;
        }
    }

    private async Task<JsonObject> BakeEffectPrefixesAsync(HybridBakeRequest request, IProgress<RenderProgress>? progress,
        StageTiming timing, CancellationToken cancellationToken)
    {
        HybridPlanFormat.Validate(request.Plan);
        if (request.Plan["blockers"] is not JsonArray { Count: 0 })
            throw new InvalidDataException("Resolve the plan's listed blockers before generating it.");
        using var source = new ProjectSource(request.Plan["source"]!.GetValue<string>());
        string output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.OutputDirectory));
        EnsureNewDerivedOutput(source, output, "Effect-prefix output");
        string? destination = request.ProjectDirectory is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.ProjectDirectory));
        if (destination is not null)
        {
            EnsureNewDerivedOutput(source, destination, "Destination project");
            if (destination.Equals(output, StringComparison.OrdinalIgnoreCase) ||
                destination.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                output.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                source.DirectoryPath.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The destination project must be separate from source and working directories.");
        }
        JsonObject result = await new EffectPrefixBakeService(tools).BakeAsync(request, progress, timing, cancellationToken);
        AttachPlanProvenance(result, request.Plan);
        if (destination is not null && StaticOnlyBake.Finished(result["status"]?.GetValue<string>()))
        {
            using (timing.Measure(StageTiming.ProjectAssembly))
            {
                using var generated = new ProjectSource(result["project_path"]!.GetValue<string>());
                await generated.ExtractAsync(destination, cancellationToken);
            }
            result["project_path"] = destination;
            result["work_directory"] = output;
            timing.Stamp(result);
            await VideoSceneBuilder.WriteJsonAsync(Path.Combine(destination, "bake.json"), result, cancellationToken);
        }
        timing.Stamp(result);
        await File.WriteAllTextAsync(Path.Combine(output, "bake.json"), result.ToJsonString(JsonOptions), cancellationToken);
        return result;
    }

    private async Task<JsonObject> BakeOnceAsync(HybridBakeRequest request, IProgress<RenderProgress>? progress,
        StageTiming timing, CancellationToken cancellationToken)
    {
        var plan = request.Plan.DeepClone().AsObject();
        if (request.SchemaVersion != 2)
            throw new InvalidDataException("A version 2 hybrid-video bake request is required.");
        HybridPlanFormat.Validate(plan);
        if (request.ProbeFrames == 0 && plan["blockers"] is JsonArray previousBlockers)
            for (int i = previousBlockers.Count - 1; i >= 0; --i)
                if (previousBlockers[i]?.GetValue<string>() == HybridScenePlanner.MissingScriptFaultEvidenceBlocker)
                    previousBlockers.RemoveAt(i);
        if (plan["blockers"] is JsonArray { Count: > 0 }) throw new InvalidDataException("Resolve the plan's listed blockers before generating it.");
        if (HybridScenePlanner.FullFrameConflict(plan) is string initialLayoutConflict)
            throw new InvalidDataException(initialLayoutConflict);
        if (HybridScenePlanner.CompositionHierarchyConflict(plan) is string initialHierarchyConflict)
            throw new InvalidDataException(initialHierarchyConflict);
        if (request.DeviceUuid is not null)
        {
            if (plan["settings"] is not JsonObject captureSettings) throw new InvalidDataException("Hybrid capture settings are missing.");
            captureSettings["device_uuid"] = request.DeviceUuid;
        }
        using var source = new ProjectSource(plan["source"]!.GetValue<string>());
        string sourceHash = await source.SourceHashAsync(cancellationToken);
        if (sourceHash != plan["source_sha256"]!.GetValue<string>()) throw new InvalidDataException("Source changed; analyze it again.");
        string output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.OutputDirectory));
        EnsureNewDerivedOutput(source, output, "Hybrid output");
        string? destination = request.ProjectDirectory is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.ProjectDirectory));
        if (destination is not null)
        {
            ProjectSource.EnsureNoReparsePoints(destination);
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("The destination project directory must be new.");
            foreach (string parent in new[] { source.DirectoryPath, output })
                if (destination.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
                    destination.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    parent.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The destination project must be separate from source and working directories.");
        }
        HybridAnalyzeRequest settings = plan["settings"]!.Deserialize<HybridAnalyzeRequest>(JsonOptions)
            ?? throw new InvalidDataException("Invalid capture settings.");
        if (request.ProbeFrames == 0)
        {
            JsonArray? errors = PlannedSourceScriptErrors(plan);
            if (errors is null)
            {
                string refreshOutput = output + ".analysis-refresh";
                progress?.Report(new("refreshing_script_fault_evidence", 0,
                    "Refreshing an older plan with current source script fault evidence."));
                plan = await new HybridScenePlanner(tools).AnalyzeSingleAsync(settings with {
                    Source = source.SourcePath, OutputDirectory = refreshOutput, RuntimeTraceFile = null
                    }, progress, cancellationToken);
                HybridPlanFormat.Validate(plan);
                if (plan["blockers"] is JsonArray { Count: > 0 })
                    throw new InvalidDataException("The refreshed analysis requires resolution before generation.");
                if (HybridScenePlanner.FullFrameConflict(plan) is string refreshedLayoutConflict)
                    throw new InvalidDataException(refreshedLayoutConflict);
                if (HybridScenePlanner.CompositionHierarchyConflict(plan) is string refreshedHierarchyConflict)
                    throw new InvalidDataException(refreshedHierarchyConflict);
                settings = plan["settings"]!.Deserialize<HybridAnalyzeRequest>(JsonOptions)
                    ?? throw new InvalidDataException("Refreshed capture settings are invalid.");
                errors = PlannedSourceScriptErrors(plan)
                    ?? throw new InvalidDataException("The refreshed analysis omitted source script fault evidence.");
            }
            var plannedIds = plan["layers"]!.AsArray().OfType<JsonObject>().Select(HybridScenePlanner.Id).ToHashSet();
            if (errors.OfType<JsonObject>().Any(error => HybridScenePlanner.Int(error["owner_layer_id"]) is not int owner || !plannedIds.Contains(owner)))
                throw new InvalidDataException("A source script fault lacks a known authored owner; analyze the source again before generation.");
        }
        JsonObject? residualMasking = null;
        async Task<JsonObject> NoLoopReportAsync(JsonArray? unresolved, JsonObject? residual = null,
            string status = "candidate_rejected_no_loop")
        {
            Directory.CreateDirectory(output);
            bool layoutRejected = status == ResidualMasking.LayoutRejectedStatus;
            var rejected = new JsonObject { ["schema_version"] = 2, ["artifact_kind"] = "hybrid_video_candidate",
                ["status"] = status, ["source_sha256"] = sourceHash,
                ["source_digest_scope"] = ProjectSource.DigestScope, ["plan"] = plan.DeepClone(),
                ["reason"] = residual?["reason"]?.GetValue<string>() ?? (unresolved is { Count: > 0 }
                    ? "The analytic loop parse left unresolved temporal components. No video was cut or project generated."
                    : "No analytic loop candidate was found. No video was cut or project generated."),
                ["frames"] = 0, ["groups"] = new JsonArray(), ["loop_validation"] = layoutRejected ? "not_performed" : "no_suitable_loop",
                ["official_playback"] = "not_verified", ["measured_gain"] = "not_verified" };
            if (layoutRejected)
            {
                rejected["reason_zh"] = residual?["reason_zh"]?.DeepClone();
                rejected["reason_en"] = residual?["reason_en"]?.DeepClone();
            }
            if (residual is not null) rejected["residual_masking"] = residual.DeepClone();
            AttachPlanProvenance(rejected, plan);
            timing.Stamp(rejected);
            await VideoSceneBuilder.WriteJsonAsync(Path.Combine(output, "bake.json"), rejected, cancellationToken);
            return rejected;
        }
        async Task<JsonObject?> RefreshLoopOrRejectAsync()
        {
            var runtime = JsonNode.Parse(await File.ReadAllTextAsync(plan["runtime_evidence"]!.GetValue<string>(), cancellationToken))!.AsObject();
            // Rebuild from source and current runtime evidence; never accept a saved observed cut or start.
            HybridScenePlanner.RefreshLoop(plan, source, runtime, settings);
            JsonArray? candidates = plan["loop"]?["candidates"] as JsonArray;
            JsonArray? unresolved = plan["loop"]?["unresolved"] as JsonArray;
            residualMasking = null;
            if (candidates is not { Count: > 0 }) return await NoLoopReportAsync(unresolved);
            if (unresolved is not { Count: > 0 }) return null;
            // 解析候选成立但留着未解析分量：逐条判定残差能不能被固定窗口的接缝淡化掩盖。
            // 判据在 ResidualMasking 里，拒绝要说清是哪一层、哪个机制、缺什么证明。
            JsonObject classification = ResidualMasking.Classify(plan, source.ReadJson(source.SceneResource),
                ResidualMasking.ResourceReader(source, settings.Assets));
            plan["loop"]!["residual_masking"] = classification.DeepClone();
            if (classification["status"]?.GetValue<string>() != "residual_maskable")
                return await NoLoopReportAsync(unresolved, classification);
            // 防御：透明组与多组都能淡化，布局淡化不了的只有"可掩盖分量不在任何视频组里"；analyze 已把它写成 blocker，
            // 旧版计划或界面改过分配的计划仍可能走到这里。在合成校验与任何渲染之前干净拒绝，写 bake.json，不抛异常，也不自动换分配。
            if (!ResidualMasking.LayoutAllowsMasking(plan, classification))
            {
                JsonObject layout = ResidualMasking.LayoutRejection(plan, classification);
                classification["status"] = "rejected_layout";
                classification["reason"] = layout["reason"]!.DeepClone();
                classification["reason_zh"] = layout["reason_zh"]!.DeepClone();
                classification["reason_en"] = layout["reason_en"]!.DeepClone();
                classification["layout_gate"] = layout;
                plan["loop"]!["residual_masking"] = classification.DeepClone();
                return await NoLoopReportAsync(unresolved, classification, ResidualMasking.LayoutRejectedStatus);
            }
            residualMasking = classification;
            return null;
        }
        if (request.ProbeFrames == 0 && await RefreshLoopOrRejectAsync() is JsonObject initialLoopRejection)
            return initialLoopRejection;
        ulong frames = request.ProbeFrames > 0 ? request.ProbeFrames :
            (plan["loop"]?["candidates"] as JsonArray)?.FirstOrDefault()?["frames"]?.GetValue<ulong>() ?? 0;
        if (frames == 0) return await NoLoopReportAsync(null);
        // 磁盘闸门：中间产物峰值按计划预估，空间不够就在任何渲染开始前干净拒绝（见 BakeDiskBudget）。
        // 探针只有几十帧，不值得为它拦一次；真正的量在主渲染上。
        if (request.ProbeFrames == 0 &&
            BakeDiskBudget.Reject(plan, frames, request.GroupParallel, output) is JsonObject diskRejection)
        {
            Directory.CreateDirectory(output);
            var rejected = new JsonObject
            {
                ["schema_version"] = 2, ["artifact_kind"] = "hybrid_video_candidate", ["status"] = BakeDiskBudget.RejectedBakeStatus,
                ["source_sha256"] = sourceHash, ["source_digest_scope"] = ProjectSource.DigestScope, ["plan"] = plan,
                ["reason"] = diskRejection["reason"]?.DeepClone(),
                ["reason_localized"] = diskRejection["reason_localized"]?.DeepClone(),
                ["disk_budget"] = diskRejection,
                ["frames"] = 0, ["groups"] = new JsonArray(), ["loop_validation"] = "not_performed",
                ["official_playback"] = "not_verified", ["measured_gain"] = "not_verified"
            };
            AttachPlanProvenance(rejected, plan);
            timing.Stamp(rejected);
            await VideoSceneBuilder.WriteJsonAsync(Path.Combine(output, "bake.json"), rejected, cancellationToken);
            return rejected;
        }
        // 锁定周期的粒子层（封顶 + 确定寿命）只在循环长度是替换周期的整数倍时同相位：analyze 的求解器已保证，这里防御旧版或改过的计划。
        if (request.ProbeFrames == 0 && residualMasking is not null &&
            ResidualMasking.LockedCycleMismatch(residualMasking, frames, settings.FpsNumerator, settings.FpsDenominator) is JsonObject cycleMismatch)
        {
            residualMasking["status"] = "rejected_particle_cycle";
            residualMasking["reason"] = cycleMismatch["reason"]!.DeepClone();
            residualMasking["particle_cycle_mismatch"] = cycleMismatch;
            plan["loop"]!["residual_masking"] = residualMasking.DeepClone();
            return await NoLoopReportAsync(plan["loop"]?["unresolved"] as JsonArray, residualMasking);
        }
        JsonObject? compositionValidation = null;
        if (request.ProbeFrames == 0)
        {
            using (timing.Measure(StageTiming.CompositionValidation))
                compositionValidation = await ValidateCompositionAsync(request, plan, settings, source, output, progress, cancellationToken);
            if (compositionValidation["status"]?.GetValue<string>() != "composition_pass")
            {
                Directory.CreateDirectory(output);
                bool scriptErrorsRejected = compositionValidation["status"]?.GetValue<string>() == CandidateScriptErrorGate.RejectedCompositionStatus;
                var rejected = new JsonObject
                {
                    ["schema_version"] = 2, ["artifact_kind"] = "hybrid_video_candidate",
                    ["status"] = scriptErrorsRejected ? CandidateScriptErrorGate.RejectedBakeStatus : "candidate_rejected_composition",
                    ["source_sha256"] = sourceHash,
                    ["source_digest_scope"] = ProjectSource.DigestScope, ["plan"] = plan,
                    ["reason"] = compositionValidation["reason"]?.DeepClone(),
                    ["metrics"] = compositionValidation["metrics"]?.DeepClone(),
                    ["composition_validation"] = compositionValidation,
                    ["probe_paths"] = new JsonObject
                    {
                        ["output"] = compositionValidation["probe_output_path"]?.DeepClone(),
                        ["capture_source"] = compositionValidation["probe_capture_source_path"]?.DeepClone(),
                        ["reference"] = compositionValidation["comparison_reference_path"]?.DeepClone(),
                        ["project"] = compositionValidation["probe_project_path"]?.DeepClone(),
                        ["comparison"] = compositionValidation["comparison_report_path"]?.DeepClone()
                    },
                    ["frames"] = 0, ["groups"] = new JsonArray(), ["loop_validation"] = "not_performed",
                    ["official_playback"] = "not_verified", ["measured_gain"] = "not_verified"
                };
                if (scriptErrorsRejected)
                {
                    rejected["reason_zh"] = compositionValidation["reason_zh"]?.DeepClone();
                    rejected["reason_en"] = compositionValidation["reason_en"]?.DeepClone();
                }
                AttachPlanProvenance(rejected, plan);
                timing.Stamp(rejected);
                await VideoSceneBuilder.WriteJsonAsync(Path.Combine(output, "bake.json"), rejected, cancellationToken);
                return rejected;
            }
        }
        // 内嵌视频大小（WPE 实测 2 GiB 上限，见 EmbeddedVideoBudget）：起点搜索与主渲染之前按试编码外推，超限就干净拒绝，
        // 不再跑完几个小时才在装配时失败。外推读不到时只记录、不拒绝，编码后还有一次按实际字节的检查。
        JsonObject? embeddedVideoEstimate = null;
        if (request.ProbeFrames == 0 && compositionValidation is not null)
        {
            embeddedVideoEstimate = await EstimateEmbeddedVideoAsync(compositionValidation, frames, settings, cancellationToken);
            if (embeddedVideoEstimate["status"]?.GetValue<string>() == "predicted_over_limit")
            {
                Directory.CreateDirectory(output);
                var rejected = new JsonObject
                {
                    ["schema_version"] = 2, ["artifact_kind"] = "hybrid_video_candidate", ["status"] = EmbeddedVideoBudget.RejectedBakeStatus,
                    ["source_sha256"] = sourceHash, ["source_digest_scope"] = ProjectSource.DigestScope, ["plan"] = plan,
                    ["reason"] = embeddedVideoEstimate["reason"]?.DeepClone(),
                    ["reason_localized"] = embeddedVideoEstimate["reason_localized"]?.DeepClone(),
                    ["composition_validation"] = compositionValidation, ["embedded_video_estimate"] = embeddedVideoEstimate,
                    ["frames"] = 0, ["groups"] = new JsonArray(), ["loop_validation"] = "not_performed",
                    ["official_playback"] = "not_verified", ["measured_gain"] = "not_verified"
                };
                AttachPlanProvenance(rejected, plan);
                timing.Stamp(rejected);
                await VideoSceneBuilder.WriteJsonAsync(Path.Combine(output, "bake.json"), rejected, cancellationToken);
                return rejected;
            }
        }
        var original = source.ReadJson(source.SceneResource);
        HybridScenePlanner.ApplyAudioEffectChoice(original, plan);
        var metadata = source.Contains("project.json") ? source.ReadJson("project.json") : new JsonObject();
        var originalObjects = original["objects"]!.AsArray().OfType<JsonObject>().ToDictionary(HybridScenePlanner.Id);
        var groups = plan["video_groups"]!.AsArray().OfType<JsonObject>().ToArray();
        var plannedLiveIds = (plan["live_layer_ids"] as JsonArray ?? []).Select(node => node!.GetValue<int>()).ToHashSet();
        // 成品编码的跨进程槽位配额：整案 CPU 峰值都在这一路 ffmpeg 上，多槽跑批时按配额排队。
        var encodeSlots = EncodeSlots.Create(request.EncodeSlots);
        // 组内并行只提前跑主渲染：最多这么多个组的渲染同时在飞，判定、编码与写入仍严格按组序串行。
        // 1 = 与改动前逐组串行完全一致，核显机器上默认不变。
        int groupParallel = Math.Clamp(request.GroupParallel, 1, Math.Max(1, groups.Length));
        Directory.CreateDirectory(output);
        var report = new JsonObject {
            ["schema_version"] = 2, ["artifact_kind"] = request.ProbeFrames > 0 ? "hybrid_video_probe" : "hybrid_video_candidate",
            ["status"] = "running", ["source_sha256"] = sourceHash, ["source_digest_scope"] = ProjectSource.DigestScope,
            ["frames"] = frames, ["plan"] = plan.DeepClone(), ["groups"] = new JsonArray(),
            ["official_playback"] = "not_verified", ["measured_gain"] = "not_verified", ["loop_validation"] = "not_performed",
            ["source_start_frame"] = 0,
            // 这两项记下本次实际生效的并行设置，事后核对每案耗时时不用再翻命令行。
            ["encode_slots"] = request.EncodeSlots, ["group_parallel"] = groupParallel,
            ["seam_policy"] = residualMasking is null ? "source_period_no_repair" : ResidualMasking.SeamPolicy };
        if (residualMasking is not null)
        {
            report["residual_masking"] = residualMasking.DeepClone();
            report["crossfade_frames"] = ResidualMasking.CrossfadeFrames(settings.FpsNumerator, settings.FpsDenominator);
        }
        AttachPlanProvenance(report, plan);
        if (compositionValidation is not null) report["composition_validation"] = compositionValidation;
        if (embeddedVideoEstimate is not null) report["embedded_video_estimate"] = embeddedVideoEstimate;
        var fullCaptureScriptErrors = new JsonArray();
        var fullCaptureScriptErrorKeys = new HashSet<string>(StringComparer.Ordinal);
        if (request.ProbeFrames == 0)
        {
            report["full_capture_source_script_error_count"] = 0;
            report["full_capture_source_script_errors"] = fullCaptureScriptErrors;
        }
        string reportPath = Path.Combine(output, "bake.json");
        async Task Save()
        {
            timing.Stamp(report);
            await File.WriteAllTextAsync(reportPath, report.ToJsonString(JsonOptions), CancellationToken.None);
        }
        await Save();
        // 提前启动的组主渲染登记表：换起点、换候选或收尾时先取消并等这些渲染器退出，再动组目录。
        // groupParallel == 1 时永远只登记当前这一组，等于没有提前启动。
        var groupRenders = new Dictionary<int, Task<JsonObject>>();
        var groupRenderCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        async Task ResetGroupRendersAsync()
        {
            if (groupRenders.Count == 0) return;
            await groupRenderCancellation.CancelAsync();
            // 这些结果本轮已经不要了：取消、失败都咽掉，真正的失败早就从主流程抛出去过一次。
            foreach (var pending in groupRenders.Values) try { await pending; } catch { }
            groupRenders.Clear();
            groupRenderCancellation.Dispose();
            groupRenderCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }
        try
        {
            var initialRuntime = JsonNode.Parse(await File.ReadAllTextAsync(plan["runtime_evidence"]!.GetValue<string>(), cancellationToken))!.AsObject();
            var runtimeDependencies = initialRuntime["runtime_dependencies"]?.AsArray()
                ?? throw new InvalidDataException("Runtime dependencies are missing.");
            var finalDependencies = MergeRuntimeDependencies(runtimeDependencies, new JsonArray());
            string captureProject = Path.Combine(output, "capture-source");
            using (timing.Measure(StageTiming.SourceCapture)) await source.ExtractAsync(captureProject, cancellationToken);
            var snapshot = plan["snapshot_properties"]!.DeepClone().AsObject();
            async Task RebuildCaptureSourceAsync()
            {
                var captureScene = original.DeepClone().AsObject();
                HybridScenePlanner.FreezeTemporalProperties(captureScene, snapshot);
                HybridScenePlanner.ApplySnapshotOmissions(captureScene, plan);
                // feat/daytime-split：按状态规划的 plan，录制副本冻结在该状态（选择器脚本不跑、本状态图层写死可见）。
                DaytimeSplit.ApplyState(captureScene, plan);
                if (settings.ViewMode == "fixed_view") captureScene["general"]!["cameraparallax"] = false;
                // Camera shake stays in the live output camera and must not also be in its videos.
                captureScene["general"]!["camerashake"] = false;
                if (request.ProbeFrames == 0) HybridLoopService.ApplyPatches(captureScene, plan["loop"]!.AsObject());
                await File.WriteAllTextAsync(ProjectSource.ContainedPath(captureProject, source.SceneResource),
                    captureScene.ToJsonString(), cancellationToken);
                // 摆动改频：按首个候选的 sway_retime 在捕获项目副本里写覆盖 shader，只影响捕获；成品项目另从源解包。
                // 改频开启时每个候选都带 sway_retime（没有解的候选在分析里已丢掉），换候选重建时整份覆盖文件随之重写。
                if (request.ProbeFrames == 0 && plan["loop"]?["candidates"]?.AsArray().FirstOrDefault()?["sway_retime"] is JsonObject swayRetime)
                {
                    report["sway_retime"] = swayRetime.DeepClone();
                    report["sway_shader_patches"] = await ShaderTextPatch.WriteSwayRetimeAsync(captureProject, source, settings.Assets,
                        plan["loop"]!.AsObject(), cancellationToken);
                }
                ApplyPropertySnapshot(metadata, snapshot);
                metadata["file"] = source.SceneResource;
                await File.WriteAllTextAsync(Path.Combine(captureProject, "project.json"), metadata.ToJsonString(), cancellationToken);
            }
            using (timing.Measure(StageTiming.SourceCapture)) await RebuildCaptureSourceAsync();
            string project = Path.Combine(output, "project");
            using (timing.Measure(StageTiming.SourceCapture)) await source.ExtractAsync(project, cancellationToken);
            var scene = original.DeepClone().AsObject();
            if (settings.ViewMode == "fixed_view") scene["general"]!["cameraparallax"] = false;
            var projection = plan["projection"]!.AsObject();
            double visibleWidth = projection["visible_width"]!.GetValue<double>(), visibleHeight = projection["visible_height"]!.GetValue<double>();
            double centerX = projection["center_x"]!.GetValue<double>(), centerY = projection["center_y"]!.GetValue<double>();
            bool preserveParallax = plan["has_parallax"]?.GetValue<bool>() == true && settings.ViewMode == "preserve";
            int nextId = checked(originalObjects.Keys.Max() + 1);
            var replacements = new Dictionary<string, JsonObject>();
            int staticLayers = 0;
            // 残差掩盖打开时：起点帧由解析周期内的接缝残差决定，成品在接缝处做固定窗口的整帧交叉淡化。
            // startFrame 是预热之后、解析周期内的相位；渲染器实际跳过的帧数是 warmupFrames + startFrame。
            ulong startFrame = 0;
            uint crossfadeFrames = residualMasking is null ? 0
                : ResidualMasking.CrossfadeFrames(settings.FpsNumerator, settings.FpsDenominator);
            // 平稳随机粒子要跑过预热（所有可掩盖粒子层 warmup_seconds 的最大值）才进入与时间无关的分布；没有粒子层时为 0。
            ulong warmupFrames = residualMasking is null ? 0
                : ResidualMasking.WarmupFrames(residualMasking, settings.FpsNumerator, settings.FpsDenominator);
            if (residualMasking is not null)
            {
                report["particle_warmup_frames"] = warmupFrames;
                report["particle_warmup_seconds"] = residualMasking["max_warmup_seconds"]?.DeepClone();
            }
            JsonObject? startSearch = null;
            var runner = new NativeRenderRunner(tools);
            // 源周期路线（无残差掩盖、非探针）在编码后接缝校验失败时，按 plan.loop.candidates 的现有顺序换下一个
            // 解析候选重跑主渲染与校验，最多 LoopCandidateFallback.MaximumAttempts 个；每次的候选与接缝读数都记进
            // loop_candidate_attempts。周期仍全部来自解析，这里只是在解析给出的候选表里往下走，不搜周期、不放宽阈值。
            JsonArray loopCandidates = plan["loop"]?["candidates"] as JsonArray ?? new JsonArray();
            bool candidateFallbackEnabled = residualMasking is null && request.ProbeFrames == 0 && loopCandidates.Count > 0;
            var candidateAttempts = new JsonArray();
            int selectedCandidateAttempt = 0;
            if (candidateFallbackEnabled) report["loop_candidate_attempts"] = candidateAttempts;
            // 残差掩盖路线的起点回退（与上面的候选回退互斥：一个只在源周期路线、一个只在残差路线）：全分辨率第一层在某个起点上
            // 被拒时，按起点搜索的排序依次换下一个候选起点，重渲所有组再测，最多 ResidualMasking.MaximumStartAttempts 个；
            // 每次的起点与各组 max_k / 整幅记进 loop_start_attempts。周期与阈值都不变，只换相位。
            bool startFallbackEnabled = residualMasking is not null && request.ProbeFrames == 0;
            var startAttempts = new JsonArray();
            JsonObject[] startOrder = [];
            if (startFallbackEnabled) report["loop_start_attempts"] = startAttempts;
            // 含可掩盖残差层的组：它们的 master 多渲一个淡化窗口、各自测第一层并淡化；其余组照常渲 P 帧，相位同样是 warmup + S。
            // 布局门保证每个残差层都在某个组里，所以残差路线下这个列表非空。
            int[] residualGroupIndexes = residualMasking is null ? [] : ResidualMasking.ResidualGroupIndexes(plan, residualMasking);
            if (residualMasking is not null && residualGroupIndexes.Length == 0)
                throw new InvalidOperationException("残差掩盖路线没有任何视频组含可掩盖残差层；布局门本应先拒绝。");
            if (residualMasking is not null)
                report["residual_group_ids"] = new JsonArray([.. residualGroupIndexes.Select(index => groups[index]["id"]!.DeepClone())]);
            (int[] Layers, bool SceneClear, double Width, double Height, uint PixelWidth, uint PixelHeight) GroupCapture(JsonObject group)
            {
                var viewport = HybridVideoProjection.CaptureViewportForGroup(projection, group, preserveParallax);
                uint pixelWidth = checked((uint)Math.Ceiling(settings.Width * viewport.Width / visibleWidth / 2) * 2);
                uint pixelHeight = checked((uint)Math.Ceiling(settings.Height * viewport.Height / visibleHeight / 2) * 2);
                return (group["layer_ids"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray(),
                    group["include_scene_clear"]?.GetValue<bool>() == true, viewport.Width, viewport.Height, pixelWidth, pixelHeight);
            }
            // 起点只在第一个含残差层的组上搜一次，起点共享给所有组：各组相位一致，组间确定性分量的相位关系与原作相同。
            // 放在任何组渲染之前，排在它前面的组也用同一个起点。
            async Task SearchSharedLoopStartAsync()
            {
                JsonObject searchGroup = groups[residualGroupIndexes[0]];
                string searchId = searchGroup["id"]!.GetValue<string>();
                var capture = GroupCapture(searchGroup);
                if (capture.PixelWidth > 8192 || capture.PixelHeight > 8192)
                    throw new InvalidDataException("The requested parallax overscan exceeds the supported texture dimensions.");
                if (searchId != Path.GetFileName(searchId)) throw new InvalidDataException("Video group IDs must be single path components.");
                // 采样步长取 gcd(P, 16)：周期不是 16 的倍数时步长缩小、候选变多，不再因对齐问题抛异常。
                uint sampleStride = ResidualMasking.StartSearchStride(frames);
                // 起点搜索与 master 用同一个预热基准（精灵整周期预热 + 粒子预热），样本第 s 帧就是 master 起点取 s 时的第 0 帧。
                ulong searchWarmupFrames = LoopWarmup.BaseFrames(LoopWarmup.CandidateSourcePeriodWarmupFrames(loopCandidates), frames, warmupFrames);
                // 搜索窗固定 2 个周期：候选起点只有 P/stride 个，每个比较 (s, s+P)，全部落在前 2P 帧里，
                // 更宽的窗口不会引入新候选。透明组用带覆盖度的样本，两半分别算。
                progress?.Report(new("searching_loop_start", 0,
                    $"在锁定的解析周期内按接缝残差挑选起点帧（组 {searchId}，预热 {searchWarmupFrames} 帧，步长 {sampleStride} 帧，搜索窗 {ResidualMasking.SearchWindowPeriods} 个周期）。"));
                using (timing.Measure(StageTiming.LoopStartSearch))
                startSearch = await runner.SearchLoopStartAsync(new(captureProject, settings.Assets,
                    ProjectSource.ContainedPath(output, $"{searchId}.start-search"), capture.PixelWidth, capture.PixelHeight,
                    settings.FpsNumerator, settings.FpsDenominator, checked(frames * (ulong)ResidualMasking.SearchWindowPeriods), WarmupFrames: searchWarmupFrames,
                    Seed: 17, UserProperties: snapshot, PixelPacking: capture.SceneClear ? "rgb" : "rgba_side_by_side",
                    DeviceUuid: request.DeviceUuid ?? settings.DeviceUuid,
                    Input: new JsonObject { ["cursor_x"] = .5, ["cursor_y"] = .5, ["cursor_in_window"] = true },
                    OrthographicCaptureViewport: new(centerX, centerY, capture.Width, capture.Height),
                    LayerSelection: new(capture.Layers, TransparentBackground: !capture.SceneClear, IncludePostprocessing: false),
                    FrameSampleStride: sampleStride,
                    FrameSampleWidth: ResidualMasking.StartSearchSampleWidth,
                    FrameSamplesOnly: true,
                    FrameSampleIncludeAlpha: !capture.SceneClear,
                    OfflineVideoRateOverrides: SelectVideoRateOverrides(plan["loop"]!.AsObject(), capture.Layers.ToHashSet())),
                    frames, crossfadeFrames, progress, cancellationToken);
                startSearch["group_id"] = searchId;
                startFrame = startSearch["selected_start_frame"]!.GetValue<ulong>();
                startOrder = ResidualStartFallback.Order(startSearch);
                report["loop_start_search"] = startSearch.DeepClone();
                report["source_start_frame"] = checked(warmupFrames + startFrame);
                report["loop_start_phase_frame"] = startFrame;
                await Save();
            }
            // 一个组的渲染帧数与预热：组循环和提前启动的渲染共用一份，两处算法不会漂开。
            (bool Residual, bool ClosureJudged, ulong RenderedFrames, ulong SourcePeriodWarmupFrames, ulong MasterWarmupFrames)
                GroupFraming(int index)
            {
                bool residual = residualMasking is not null && request.ProbeFrames == 0 && residualGroupIndexes.Contains(index);
                bool closureJudged = request.ProbeFrames == 0 && !residual;
                ulong rendered = residual ? checked(frames + crossfadeFrames) : closureJudged ? checked(frames + 1) : frames;
                ulong sourcePeriodWarmup = request.ProbeFrames == 0 ? LoopWarmup.CandidateSourcePeriodWarmupFrames(loopCandidates) : 0;
                return (residual, closureJudged, rendered, sourcePeriodWarmup,
                    LoopWarmup.MasterFrames(sourcePeriodWarmup, frames, warmupFrames, startFrame));
            }
            // 直编组要在渲染开始前就定下播放档位（编码器随渲染一起启动），所以档位解析提到所有渲染之前，整次烘焙只解析一次。
            // master 路线仍把原字符串交给 EncodeCroppedRgbaAsync 自己解析，行为不变。软件档位不起额外进程。
            // 放在 MasterRenderRequest 之前：局部函数不能引用在它之后声明的局部变量。
            (string playbackKind, string? playbackFallbackReason) =
                await runner.ResolvePlaybackEncoderAsync(request.PlaybackEncoder, output, cancellationToken);
            // 一个组的主渲染请求。只依赖本轮固定的量（frames、startFrame、候选表），所以可以提前给后面的组用；
            // 换轮次时登记表会先排空，下一轮按新的量重造。
            RenderRequest MasterRenderRequest(int index)
            {
                var group = groups[index];
                string groupId = group["id"]!.GetValue<string>();
                var capture = GroupCapture(group);
                if (capture.PixelWidth > 8192 || capture.PixelHeight > 8192)
                    throw new InvalidDataException("The requested parallax overscan exceeds the supported texture dimensions.");
                if (groupId != Path.GetFileName(groupId)) throw new InvalidDataException("Video group IDs must be single path components.");
                var framing = GroupFraming(index);
                // 直编组不写无损 master：渲染器出帧直接进播放档编码器，成品就是这一遍的产物。
                // 判定与组循环里的 directPlayback 走同一个函数，提前启动的渲染与轮到它时的处理口径一致。
                bool direct = AllowsDirectPlayback(capture.SceneClear, framing.Residual, request.ProbeFrames);
                return new(captureProject, settings.Assets, Path.Combine(ProjectSource.ContainedPath(output, groupId), "master"),
                    capture.PixelWidth, capture.PixelHeight, settings.FpsNumerator, settings.FpsDenominator,
                    framing.RenderedFrames, WarmupFrames: framing.MasterWarmupFrames,
                    Seed: 17, UserProperties: snapshot, PixelPacking: capture.SceneClear ? "rgb" : "rgba_side_by_side",
                    LosslessTest: !direct, PlaybackEncoderKind: direct ? playbackKind : null,
                    DeviceUuid: request.DeviceUuid ?? settings.DeviceUuid, CollectAlphaBounds: true, BoundsIncludeRgb: true,
                    Input: new JsonObject { ["cursor_x"] = .5, ["cursor_y"] = .5, ["cursor_in_window"] = true },
                    OrthographicCaptureViewport: new(centerX, centerY, capture.Width, capture.Height),
                    LayerSelection: new(capture.Layers, TransparentBackground: !capture.SceneClear, IncludePostprocessing: false),
                    TraceScene: request.ProbeFrames == 0,
                    ForceKeyFrameFrame: framing.Residual ? crossfadeFrames : null,
                    OfflineVideoRateOverrides: request.ProbeFrames == 0
                        ? SelectVideoRateOverrides(plan["loop"]!.AsObject(), capture.Layers.ToHashSet()) : null,
                    EncodedFrames: framing.ClosureJudged ? frames : null,
                    RetainFrames: request.ProbeFrames == 0
                        ? candidateFallbackEnabled ? SourceStartOffset.RetainedFrameIndices(frames) : LoopClosureCheck.ReferenceFrameIndices(frames)
                        : null);
            }
            // 异步方法：构造请求时抛出的异常留在任务里，等轮到这个组时才浮出来，不会打乱前面组的判定顺序。
            async Task<JsonObject> StartGroupRenderAsync(int index)
            {
                RenderRequest render = MasterRenderRequest(index);
                if (Directory.Exists(render.OutputDirectory) || File.Exists(render.OutputDirectory))
                    throw new IOException("A group master output must be new; existing files will not be cleaned.");
                return await runner.RenderAsync(render, progress, groupRenderCancellation.Token);
            }
            // 取这个组的主渲染，并按 groupParallel 把后面几组的渲染提前挂上去。
            Task<JsonObject> GroupRenderAsync(int index)
            {
                if (!groupRenders.TryGetValue(index, out Task<JsonObject>? render))
                    groupRenders[index] = render = StartGroupRenderAsync(index);
                for (int ahead = index + 1; ahead < Math.Min(groups.Length, index + groupParallel); ++ahead)
                    if (!groupRenders.ContainsKey(ahead)) groupRenders[ahead] = StartGroupRenderAsync(ahead);
                return render;
            }
            // 源周期路线的起点偏移（见 SourceStartOffset）：同一个候选第 0 帧是孤立起点异常帧时，整轮换起点 S = 1 重渲，
            // 不消耗候选回退次数；换候选时起点回到 0，由新候选自己的原帧重新判。每个判过的记录都进 source_start_offset_checks。
            bool startOffsetRetryPending = false;
            var startOffsetChecks = new JsonArray();
            for (int candidateAttempt = 0; ; ++candidateAttempt)
            {
            bool startOffsetRound = startOffsetRetryPending;
            startOffsetRetryPending = false;
            if (candidateAttempt > 0 || startOffsetRound)
            {
                // 换候选：把它换到候选表首位（所有读 candidates[0] 的地方随之一致），重建捕获场景与成品目录，
                // 清空上一轮的组记录。上一轮的组输出目录已在下面删掉，磁盘峰值不叠加。
                // 残差路线换的是起点：候选表与捕获场景不变，只换 startFrame。
                // 起点偏移重试：候选与捕获场景都不变，startFrame 已在触发处设好，这里只清上一轮。
                if (startOffsetRound)
                {
                    report["source_start_frame"] = checked(warmupFrames + startFrame);
                    progress?.Report(new("retrying_source_start_offset", 0, Messages.Get("progress.retrying_source_start_offset",
                        Messages.DefaultLanguage(), frames, startFrame)));
                }
                else if (candidateFallbackEnabled)
                {
                    LoopCandidateFallback.Promote(loopCandidates, candidateAttempt);
                    frames = loopCandidates[0]!["frames"]!.GetValue<ulong>();
                    report["frames"] = frames;
                    report["plan"] = plan.DeepClone();
                    startFrame = 0;
                    report["source_start_frame"] = 0UL;
                    report.Remove("source_start_offset");
                    await RebuildCaptureSourceAsync();
                }
                else
                {
                    ulong rejectedStart = startFrame;
                    startFrame = startOrder[candidateAttempt]["start_frame"]!.GetValue<ulong>();
                    report["source_start_frame"] = checked(warmupFrames + startFrame);
                    report["loop_start_phase_frame"] = startFrame;
                    report.Remove("reason_localized");
                    report.Remove("seam_residual");
                    report.Remove("loop_crossfade");
                    progress?.Report(new("retrying_next_loop_start", 0, Messages.Get("progress.retrying_next_loop_start",
                        Messages.DefaultLanguage(), rejectedStart, candidateAttempt + 1, startFrame)));
                }
                if (Directory.Exists(project)) Directory.Delete(project, recursive: true);
                await source.ExtractAsync(project, cancellationToken);
                replacements.Clear();
                staticLayers = 0;
                nextId = checked(originalObjects.Keys.Max() + 1);
                finalDependencies = MergeRuntimeDependencies(runtimeDependencies, new JsonArray());
                fullCaptureScriptErrors.Clear();
                fullCaptureScriptErrorKeys.Clear();
                report["full_capture_source_script_error_count"] = 0;
                report["groups"] = new JsonArray();
                // 换候选/换起点前先把磁盘上的报告标成"未完成"并记下候选序号：这一轮要跑很久，
                // 期间被硬超时或硬杀掉时留下的就是 in_progress，而不是上一轮写下的结论。
                MarkInProgress(report, candidateAttempt, startOffsetRound ? "source_start_offset_retry"
                    : candidateFallbackEnabled ? "loop_candidate_fallback" : "loop_start_fallback");
                if (candidateFallbackEnabled && !startOffsetRound)
                    progress?.Report(new("retrying_next_loop_candidate", 0,
                        $"Analytic candidate #{candidateAttempt} ({frames} frames) after the previous candidate failed the encoded seam check."));
                await Save();
            }
            bool retryNextCandidate = false, retryNextStart = false;
            // 这一轮各残差组的第一层读数，组成本次起点尝试的记录。
            var roundResiduals = new List<(string GroupId, JsonObject SeamResidual)>();
            if (residualMasking is not null && request.ProbeFrames == 0 && startSearch is null) await SearchSharedLoopStartAsync();
            for (int i = 0; i < groups.Length; ++i)
            {
                var group = groups[i];
                string id = group["id"]!.GetValue<string>();
                bool includeSceneClear = group["include_scene_clear"]?.GetValue<bool>() == true;
                int[] layers = group["layer_ids"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray();
                double depthX = preserveParallax ? group["parallax_depth"]![0]!.GetValue<double>() : 0;
                double depthY = preserveParallax ? group["parallax_depth"]![1]!.GetValue<double>() : 0;
                var viewport = HybridVideoProjection.CaptureViewportForGroup(projection, group, preserveParallax);
                double captureWidth = viewport.Width, captureHeight = viewport.Height;
                uint pixelWidth = checked((uint)Math.Ceiling(settings.Width * captureWidth / visibleWidth / 2) * 2);
                uint pixelHeight = checked((uint)Math.Ceiling(settings.Height * captureHeight / visibleHeight / 2) * 2);
                if (pixelWidth > 8192 || pixelHeight > 8192) throw new InvalidDataException("The requested parallax overscan exceeds the supported texture dimensions.");
                if (id != Path.GetFileName(id)) throw new InvalidDataException("Video group IDs must be single path components.");
                string work = ProjectSource.ContainedPath(output, id);
                progress?.Report(new("rendering_group", (double)i / groups.Length, $"Video group {i + 1}/{groups.Length}: {layers.Length} source layers"));
                string masterPath = Path.Combine(work, "master");
                // 提前启动的渲染已经建好这个目录并自己查过一次；没有提前启动时照旧在这里查。
                if (!groupRenders.ContainsKey(i) && (Directory.Exists(masterPath) || File.Exists(masterPath)))
                    throw new IOException("A group master output must be new; existing files will not be cleaned.");
                // 这个组含可掩盖残差层：多渲一个淡化窗口，测第一层，通过后在接缝处淡化。
                bool residualGroup = residualMasking is not null && request.ProbeFrames == 0 && residualGroupIndexes.Contains(i);
                // 这个组的成品能不能在渲染时直接编出来，不写无损 master。
                bool directPlayback = AllowsDirectPlayback(includeSceneClear, residualGroup, request.ProbeFrames);
                JsonObject? groupSeamResidual = null, groupCrossfade = null;
                bool groupFailed = false;
                try
                {
                    // 正常走不到：布局淡化不了时 RefreshLoopOrRejectAsync 已经干净拒绝。这里只留作内部不变量。
                    if (residualMasking is not null && !ResidualMasking.LayoutAllowsMasking(plan, residualMasking))
                        throw new InvalidOperationException("残差掩盖要求每个可掩盖残差层都在某个视频组里，这个计划里有残差层不在任何组中。");
                    JsonObject master;
                    // 不淡化的组（源周期路线的所有组，以及残差路线里不含残差层的组）多渲 1 帧：渲染器连续播放到第 P 帧，
                    // 编码只取前 P 帧，第 P 帧原帧留作闭合检验与接缝参照，接缝由闭合检验裁决。
                    // 残差组本来就多渲一个淡化窗口，第 P 帧同样留原帧（淡化会改写 master 的前 C 帧，f[P] 要在那之前留下）。
                    var framing = GroupFraming(i);
                    bool closureJudgedGroup = framing.ClosureJudged;
                    ulong renderedFrames = framing.RenderedFrames;
                    // 录制前跳过的帧数 = 精灵 float32 整周期预热（0 或 P，见 SpriteSeamPhase）+ 粒子预热 W + 起点 S，
                    // 两种预热取和。S 在残差路线是起点搜索选的相位，在源周期路线是起点偏移（0，或第 0 帧孤立异常时的 1，见 SourceStartOffset）。
                    // 整周期预热不改精确周期分量的相位，所以 source_start_frame 不含它：源周期路线是 S，残差路线是 W + S。
                    ulong masterWarmupFrames = framing.MasterWarmupFrames;
                    if (request.ProbeFrames == 0) report["source_period_warmup_frames"] = framing.SourcePeriodWarmupFrames;
                    // 请求本身由 MasterRenderRequest 造（淡化窗口末尾的强制 IDR、源周期路线多留的第 1、2 帧都在那里）。
                    // 提前启动过就直接等它，master_render 只计真正等的那段墙钟，与后面的编码不重叠。
                    using (timing.Measure(StageTiming.MasterRender))
                    master = await GroupRenderAsync(i);
                    timing.AddMasterBreakdown(master);
                    JsonObject? lateDependencyValidation = null;
                    if (request.ProbeFrames == 0)
                    {
                        JsonArray lateDependencies = HybridExportSafety.LateExternalDependencies(master, layers.ToHashSet(), originalObjects);
                        JsonArray captureErrors = master["native_result"] is JsonObject nativeResult ?
                            SourceScriptErrors(nativeResult) ?? throw new InvalidDataException("A full group capture omitted source script fault metadata.") :
                            throw new InvalidDataException("A full group capture omitted its native result.");
                        finalDependencies = MergeRuntimeDependencies(finalDependencies, nativeResult["runtime_dependencies"]!.AsArray());
                        foreach (var error in captureErrors.OfType<JsonObject>())
                            if (fullCaptureScriptErrorKeys.Add(error.ToJsonString())) fullCaptureScriptErrors.Add(error.DeepClone());
                        report["full_capture_source_script_error_count"] = fullCaptureScriptErrors.Count;
                        JsonObject classifiedErrors = HybridExportSafety.ClassifyLateSourceScriptErrors(captureErrors, layers.ToHashSet(), plannedLiveIds, originalObjects);
                        JsonArray unsafeScriptErrors = classifiedErrors["unsafe"]!.AsArray();
                        JsonArray externalLiveScriptErrors = classifiedErrors["external_live"]!.AsArray();
                        bool crossBoundaryWrite = lateDependencies.OfType<JsonObject>().Any(dependency => dependency["operation"]?.GetValue<string>() == "write");
                        lateDependencyValidation = new JsonObject
                        {
                            ["status"] = lateDependencies.Count == 0 && unsafeScriptErrors.Count == 0 ? "observed_no_late_external_dependency" : "rejected",
                            ["group_id"] = id,
                            ["source_layers"] = JsonSerializer.SerializeToNode(layers),
                            ["full_capture_frames"] = renderedFrames,
                            ["warmup_frames"] = masterWarmupFrames,
                            ["capture_manifest"] = Path.Combine(masterPath, "manifest.json"),
                            ["dependencies"] = lateDependencies.DeepClone(),
                            ["source_script_errors"] = unsafeScriptErrors.DeepClone(),
                            ["external_live_source_script_errors"] = externalLiveScriptErrors.DeepClone(),
                            ["scope"] = "Observed from source frame zero through the captured interval. Reject wall-clock, external-audio, pointer or media input to this group or its retained ancestors. Reject non-initialization writes from outside the group into its layers or ancestors, writes from a group owner to any known source object outside the group, and source script faults in baked content, retained ancestors or layers not already retained live. Faults in an external retained-live owner remain isolated there and are reported. This is bounded dependency evidence, not a proof about unexecuted later branches."
                        };
                        if (lateDependencies.Count > 0 || unsafeScriptErrors.Count > 0)
                        {
                            report["groups"]!.AsArray().Add(new JsonObject
                            {
                                ["id"] = id,
                                ["status"] = unsafeScriptErrors.Count > 0 ? "rejected_late_source_script_error" :
                                    crossBoundaryWrite ? "rejected_late_external_write" : "rejected_late_external_input",
                                ["source_layers"] = JsonSerializer.SerializeToNode(layers),
                                ["late_dependency_validation"] = lateDependencyValidation.DeepClone()
                            });
                            report["status"] = "candidate_rejected_late_dependency";
                            report["reason"] = unsafeScriptErrors.Count > 0
                                ? "The complete capture observed a source script fault in baked content, a retained ancestor or a layer not already retained live. Re-analyze the allocation so official fault isolation and prior property values remain live; no replacement layer was written for this group."
                                : crossBoundaryWrite
                                    ? "The complete capture observed a non-initialization write crossing the allocation boundary, either into baked content/its retained ancestors or from a baked controller to a known external source object. Removing the writer can lose live updates; retaining it can apply captured motion twice. Re-analyze the allocation; no replacement layer was written for this group."
                                    : "The complete capture observed a live external input in baked content or a retained ancestor that the bounded analysis had not protected. Re-analyze the allocation; no replacement layer was written for this group.";
                            report["late_dependency_validation"] = lateDependencyValidation;
                            report["loop_validation"] = "not_performed";
                            await Save();
                            return report;
                        }
                    }
                    if (master["alpha_bounds"]?["has_content"]?.GetValue<bool>() != true)
                    {
                        report["groups"]!.AsArray().Add(new JsonObject { ["id"] = id, ["status"] = "empty_in_generated_interval",
                            ["source_layers"] = JsonSerializer.SerializeToNode(layers), ["late_dependency_validation"] = lateDependencyValidation });
                        await Save(); continue;
                    }
                    // 起点异常判据：源周期路线、起点还是 0 时，编码之前先用原帧判。第 0 帧是孤立异常帧（闭合只因它失败）时，
                    // 这一轮到此为止，整轮换起点 S = 1 重渲；闭合通过或不是孤立异常时照常往下走，由编码后的闭合检验裁决。
                    if (candidateFallbackEnabled && closureJudgedGroup && startFrame == 0 && frames >= 3)
                    {
                        JsonObject startCheck;
                        using (timing.Measure(StageTiming.SeamCheck))
                        {
                            byte[] f0 = await LoopClosureCheck.ReadRetainedFrameAsync(master, 0, cancellationToken);
                            byte[] f1 = await LoopClosureCheck.ReadRetainedFrameAsync(master, 1, cancellationToken);
                            byte[] f2 = await LoopClosureCheck.ReadRetainedFrameAsync(master, 2, cancellationToken);
                            byte[] beforeWrap = await LoopClosureCheck.ReadRetainedFrameAsync(master, frames - 1, cancellationToken);
                            byte[] wrap = await LoopClosureCheck.ReadRetainedFrameAsync(master, frames, cancellationToken);
                            startCheck = SourceStartOffset.Evaluate(f0, f1, f2, beforeWrap, wrap, (int)pixelWidth, (int)pixelHeight,
                                withAlpha: !includeSceneClear, frames);
                        }
                        // 闭合通过的组不记，bake.json 与改动前一致；闭合失败的组不论偏不偏移都记下读数。
                        if (startCheck["status"]?.GetValue<string>() != SourceStartOffset.ClosedStatus)
                        {
                            startCheck["candidate_attempt"] = candidateAttempt;
                            startCheck["group_id"] = id;
                            startOffsetChecks.Add(startCheck.DeepClone());
                            report["source_start_offset_checks"] = startOffsetChecks.DeepClone();
                        }
                        if (SourceStartOffset.RequiresOffset(startCheck))
                        {
                            // 跳出组循环；每组的 finally 先清 master 中间文件，轮次末尾再删整个组目录。
                            startFrame = SourceStartOffset.OffsetFrames;
                            report["source_start_offset"] = SourceStartOffset.Applied(id, startCheck, startFrame);
                            startOffsetRetryPending = true;
                            await Save();
                            break;
                        }
                    }
                    if (residualGroup)
                    {
                        // 全分辨率复核第一层：降采样只用来排序候选起点，放行与否看这里的数字。
                        progress?.Report(new("checking_seam_residual", (double)i / groups.Length,
                            "在全分辨率下测量淡化窗口内每一帧的接缝残差。"));
                        JsonObject wrap;
                        using (timing.Measure(StageTiming.SeamCheck))
                            wrap = await runner.MeasureSeamResidualAsync(masterPath, frames, crossfadeFrames, cancellationToken);
                        wrap["limits"] = ResidualMasking.Thresholds();
                        bool firstLayer = wrap["first_layer"]!["passed"]!.GetValue<bool>();
                        wrap["group_id"] = id;
                        groupSeamResidual = wrap;
                        // 顶层 seam_residual 记本轮读数最差的残差组（被拒的组一定记）；每组自己的读数记在组记录里。
                        if (!firstLayer || report["seam_residual"] is not JsonObject previousResidual ||
                            wrap["first_layer"]!["maximum_worst_tile_rgb_mae_255"]!.GetValue<double>() >=
                            (previousResidual["first_layer"]?["maximum_worst_tile_rgb_mae_255"]?.GetValue<double>() ?? 0))
                            report["seam_residual"] = wrap.DeepClone();
                        roundResiduals.Add((id, wrap));
                        // 本轮最后一个残差组测完（或任何一组被拒）时，这次起点尝试就有了结论。
                        if (!firstLayer || i == residualGroupIndexes[^1])
                        {
                            startAttempts.Add(ResidualStartFallback.Attempt(candidateAttempt, startFrame,
                                startOrder.ElementAtOrDefault(candidateAttempt), roundResiduals));
                            if (firstLayer) report["selected_loop_start"] = ResidualStartFallback.Selected(candidateAttempt, startFrame);
                        }
                        if (!firstLayer && ResidualStartFallback.Next(candidateAttempt, false, startOrder.Length) == ResidualStartStep.RetryNextStart)
                        {
                            // 跳出组循环；每组的 finally 先清 master 中间文件，轮次末尾再删整个组目录，下一轮换起点重渲。
                            report["groups"]!.AsArray().Add(new JsonObject {
                                ["id"] = id, ["status"] = "rejected_seam_residual", ["storage"] = "video",
                                ["source_layers"] = JsonSerializer.SerializeToNode(layers),
                                ["seam_residual"] = wrap.DeepClone() });
                            retryNextStart = true;
                            await Save();
                            break;
                        }
                        if (!firstLayer)
                        {
                            // 残差被拒时还没有编码成品：从尚未清理的无损 master 里 seek 出硬切接缝两侧各 N 帧。
                            // 预览写在组目录，finally 只删 master 里点名的中间文件。
                            JsonObject residualPreview;
                            progress?.Report(new("exporting_seam_preview", (double)i / groups.Length,
                                Messages.Get("progress.exporting_seam_preview", Messages.DefaultLanguage(),
                                    SeamPreview.WindowFrames(frames, settings.FpsNumerator, settings.FpsDenominator))));
                            using (timing.Measure(StageTiming.SeamCheck))
                                residualPreview = await SeamPreview.ExportOrWarnAsync(report, id, SeamPreview.RejectedOutcome,
                                    token => runner.ExportSeamPreviewAsync(Path.Combine(masterPath, "preview.mp4"),
                                        Path.Combine(work, SeamPreview.FileName), frames, settings.FpsNumerator, settings.FpsDenominator,
                                        "lossless_master_hard_cut", token), cancellationToken);
                            var rejectedResidualGroup = new JsonObject {
                                ["id"] = id, ["status"] = "rejected_seam_residual", ["storage"] = "video",
                                ["source_layers"] = JsonSerializer.SerializeToNode(layers),
                                ["late_dependency_validation"] = lateDependencyValidation,
                                ["seam_residual"] = wrap.DeepClone() };
                            SeamPreview.Attach(rejectedResidualGroup, residualPreview);
                            report["groups"]!.AsArray().Add(rejectedResidualGroup);
                            report["status"] = "candidate_rejected_seam";
                            report["loop_validation"] = "residual_above_limits";
                            // 候选起点都试过了：理由列出每个起点各组的 max_k 瓦片（带 k）与 Δ_0 整幅。
                            string residualReason = ResidualStartFallback.RejectionReason(startAttempts,
                                startSearch?["candidate_count"]?.GetValue<int>() ?? startOrder.Length, crossfadeFrames);
                            report["reason"] = residualReason;
                            report["reason_localized"] = Messages.Localize(residualReason);
                            if (sourceHash != await source.SourceHashAsync(cancellationToken)) throw new IOException("Source changed during generation.");
                            await Save();
                            return report;
                        }
                        progress?.Report(new("applying_crossfade", (double)i / groups.Length,
                            "在接缝处做固定窗口的整帧交叉淡化。"));
                        using (timing.Measure(StageTiming.Crossfade))
                            groupCrossfade = await runner.ApplyLoopCrossfadeAsync(masterPath, frames, crossfadeFrames, cancellationToken);
                        groupCrossfade["group_id"] = id;
                        // 顶层 loop_crossfade 记第一个残差组；每组自己的淡化记录（含自检）在组记录里。
                        if (report["loop_crossfade"] is null) report["loop_crossfade"] = groupCrossfade.DeepClone();
                        await Save();
                    }
                    bool packedAlpha = !includeSceneClear && master["alpha_bounds"]?["minimum_alpha"]?.ToJsonString() != "255";
                    bool isStatic = master["alpha_bounds"]?["pixel_identical_in_generated_interval"]?.GetValue<bool>() == true;
                    if (settings.VideoLayout == "full_frame" && (packedAlpha || master["alpha_bounds"]?["minimum_alpha"]?.ToJsonString() != "255"))
                        throw new InvalidDataException("Full-frame output was not opaque. The candidate was stopped instead of switching to transparent video.");
                    JsonObject encoded;
                    CacheRegion crop;
                    string video;
                    if (isStatic)
                    {
                        var bounds = master["alpha_bounds"]!;
                        crop = new((int)pixelWidth, (int)pixelHeight, bounds["x"]!.GetValue<int>(), bounds["y"]!.GetValue<int>(),
                            bounds["width"]!.GetValue<int>(), bounds["height"]!.GetValue<int>());
                        byte[] first = await File.ReadAllBytesAsync(bounds["first_frame_rgba_path"]!.GetValue<string>(), cancellationToken);
                        byte[] pixels = new byte[checked(crop.Width * crop.Height * 4)];
                        for (int row = 0; row < crop.Height; ++row)
                            first.AsSpan(checked(((row + crop.Y) * (int)pixelWidth + crop.X) * 4), crop.Width * 4).CopyTo(pixels.AsSpan(row * crop.Width * 4));
                        video = Path.Combine(work, "static.rgba");
                        using (timing.Measure(StageTiming.EncodePlayback))
                            await File.WriteAllBytesAsync(video, pixels, cancellationToken);
                        encoded = new JsonObject { ["crop"] = JsonSerializer.SerializeToNode(crop, JsonOptions) };
                        ++staticLayers;
                    }
                    else
                    {
                        // 直编组的成品已经在渲染那一遍编好了：这里只把它接管进成品目录，不再解码重编，所以不计 encode_playback
                        // （它的编码墙钟与渲染重叠，已含在 master_render 里），也不占编码槽配额（它是随渲染帧率的持续负载，不是尖峰）。
                        if (directPlayback)
                            encoded = await runner.AdoptDirectPlaybackAsync(master, masterPath, Path.Combine(work, "encoded"),
                                request.PlaybackEncoder ?? PlaybackEncoderSelection.Software, playbackKind, playbackFallbackReason,
                                cancellationToken);
                        else
                        {
                            // master 路线照旧整片解码重编一遍。成品编码是整案的 CPU 峰值：按跨进程配额排队，
                            // 等槽位的时间单独计时，不混进 encode_playback。
                            using var slot = await encodeSlots.AcquireAsync(cancellationToken);
                            timing.Add(StageTiming.EncodeSlotWait, slot.WaitSeconds);
                            using (timing.Measure(StageTiming.EncodePlayback))
                                encoded = await runner.EncodeCroppedRgbaAsync(masterPath, Path.Combine(work, "encoded"), cancellationToken,
                                    preserveAlpha: packedAlpha, playbackEncoder: request.PlaybackEncoder);
                        }
                        crop = encoded["crop"]!.Deserialize<CacheRegion>(JsonOptions)!;
                        video = encoded["video_path"]!.GetValue<string>();
                    }
                    // 试编码外推偏低时的兜底：实际字节已超内嵌视频上限，WPE 放不出来，接缝、质量校验与装配都不必再做。
                    long encodedBytes = isStatic ? 0 : new FileInfo(video).Length;
                    if (request.ProbeFrames == 0 && encodedBytes > EmbeddedVideoBudget.MaximumBytes)
                    {
                        report["groups"]!.AsArray().Add(new JsonObject {
                            ["id"] = id, ["status"] = "rejected_embedded_video_size", ["storage"] = "video",
                            ["source_layers"] = JsonSerializer.SerializeToNode(layers), ["packed_alpha"] = packedAlpha,
                            ["crop"] = encoded["crop"]!.DeepClone(), ["video_path"] = video,
                            ["video_bytes"] = encodedBytes, ["maximum_bytes"] = EmbeddedVideoBudget.MaximumBytes,
                            ["late_dependency_validation"] = lateDependencyValidation,
                            ["encoded_loop_validation"] = null, ["hardware_decode"] = null });
                        string sizeReason = EmbeddedVideoBudget.EncodedRejection(id, encodedBytes, frames, settings.FpsNumerator, settings.FpsDenominator);
                        report["status"] = EmbeddedVideoBudget.RejectedBakeStatus;
                        report["reason"] = sizeReason;
                        report["reason_localized"] = Messages.Localize(sizeReason);
                        if (sourceHash != await source.SourceHashAsync(cancellationToken)) throw new IOException("Source changed during generation.");
                        await Save();
                        return report;
                    }
                    double x = ((crop.X + crop.Width / 2d) / pixelWidth - .5) * captureWidth;
                    if (settings.VideoLayout == "full_frame" && (crop.X != 0 || crop.Y != 0 || crop.Width != pixelWidth || crop.Height != pixelHeight))
                        throw new InvalidDataException("Full-frame output did not cover the complete capture. The candidate was stopped instead of selecting a smaller video region.");
                    double y = (.5 - (crop.Y + crop.Height / 2d) / pixelHeight) * captureHeight;
                    JsonObject? seam = null;
                    if (request.ProbeFrames == 0)
                    {
                        using (timing.Measure(StageTiming.SeamCheck))
                        {
                            // 闭合检验在编码前的原帧上做：第 P 帧对第 0 帧。不淡化的组据此裁决；残差组只记录（硬切残差由残差层判）。
                            // 残差路线里不含残差层的组没有残差层替它判，照源周期路线裁决。
                            byte[] firstFrame = await LoopClosureCheck.ReadRetainedFrameAsync(master, 0, cancellationToken);
                            byte[] wrapFrame = await LoopClosureCheck.ReadRetainedFrameAsync(master, frames, cancellationToken);
                            JsonObject closure = LoopClosureCheck.Evaluate(firstFrame, wrapFrame,
                                (int)pixelWidth, (int)pixelHeight, withAlpha: !includeSceneClear, frames, judged: !residualGroup);
                            if (isStatic)
                                seam = new JsonObject { ["status"] = LoopClosureCheck.Allows(closure) ? "observed_seam_pass" : "observed_seam_fail",
                                    ["failures"] = LoopClosureCheck.Allows(closure) ? new JsonArray() : new JsonArray("loop_not_closed"),
                                    ["basis"] = "Every encoded RGBA frame was byte-identical in this interval; stored as one static texture. Frame P must still close onto frame 0.",
                                    ["loop_closure"] = closure };
                            else
                                // 直编组没有无损 master 可解：m[0]、m[P−1] 直接用渲染时留下的原帧（就是编码器第 0、P−1 帧的输入）。
                                // master 是无损的，从它解出来的那两帧与原帧逐字节相同，所以判据读数与 master 路线一致。
                                seam = await EncodedLoopValidator.ValidateAsync(video, tools, frames, settings.FpsNumerator, settings.FpsDenominator,
                                    packedAlpha, directPlayback
                                        ? await EncodedLoopValidator.FromRetainedFramesAsync(master, crop, packedAlpha, frames, closure,
                                            wrapFrame, cancellationToken)
                                        : await EncodedLoopValidator.FromLosslessMasterAsync(tools, masterPath, crop, packedAlpha, frames,
                                            residualGroup ? crossfadeFrames : 0, closure, wrapFrame, cancellationToken), cancellationToken: cancellationToken);
                        }
                    }
                    else if (isStatic) seam = new JsonObject { ["status"] = "observed_seam_pass", ["basis"] = "Every captured RGBA frame was byte-identical in this probe interval; stored as one static texture." };
                    // 接缝校验到此有了结局（通过或被拒），都导出预览；失败只记 warning。
                    JsonObject? seamPreview = null;
                    if (SeamPreview.ShouldExport(request.ProbeFrames > 0, isStatic, seam))
                    {
                        progress?.Report(new("exporting_seam_preview", (double)i / groups.Length,
                            Messages.Get("progress.exporting_seam_preview", Messages.DefaultLanguage(),
                                SeamPreview.WindowFrames(frames, settings.FpsNumerator, settings.FpsDenominator))));
                        using (timing.Measure(StageTiming.SeamCheck))
                            seamPreview = await SeamPreview.ExportOrWarnAsync(report, id,
                                SeamPreview.Outcome(seam["status"]?.GetValue<string>()),
                                token => runner.ExportSeamPreviewAsync(video, Path.Combine(work, SeamPreview.FileName), frames,
                                    settings.FpsNumerator, settings.FpsDenominator, "encoded_video", token), cancellationToken);
                    }
                    JsonObject? hardwareDecode = null;
                    if (request.ProbeFrames == 0 && seam?["status"]?.GetValue<string>() != "observed_seam_pass")
                    {
                        var rejectedGroup = new JsonObject {
                            ["id"] = id, ["status"] = "rejected_seam", ["storage"] = "video",
                            ["source_layers"] = JsonSerializer.SerializeToNode(layers), ["packed_alpha"] = packedAlpha,
                            ["crop"] = encoded["crop"]!.DeepClone(), ["video_path"] = video,
                            ["late_dependency_validation"] = lateDependencyValidation,
                            ["encoded_loop_validation"] = seam,
                            ["hardware_decode"] = null };
                        SeamPreview.Attach(rejectedGroup, seamPreview);
                        report["groups"]!.AsArray().Add(rejectedGroup);
                        if (candidateFallbackEnabled)
                        {
                            candidateAttempts.Add(LoopCandidateFallback.Attempt(candidateAttempt, loopCandidates[0]!.AsObject(),
                                "rejected_seam", report["groups"]!.AsArray()));
                            if (startFrame != 0) candidateAttempts[^1]!["source_start_frame"] = startFrame;
                            if (LoopCandidateFallback.CanRetry(candidateAttempt, loopCandidates.Count))
                            {
                                // 跳出组循环；每组的 finally 先清 master 中间文件，候选循环尾再删整个组目录。
                                retryNextCandidate = true;
                                await Save();
                                break;
                            }
                        }
                        report["status"] = "candidate_rejected_seam";
                        report["loop_validation"] = "encoded_seam_failed";
                        string attempts = candidateFallbackEnabled ? " " + LoopCandidateFallback.Summary(candidateAttempts) : "";
                        string reasonEnglish = Messages.Get("bake.encoded_seam_rejected", Messages.English,
                            EncodedLoopValidator.RejectionDetail(seam!, Messages.English)) + attempts;
                        report["reason"] = reasonEnglish;
                        report["reason_localized"] = new JsonObject { ["key"] = "bake.encoded_seam_rejected",
                            ["zh"] = Messages.Get("bake.encoded_seam_rejected", Messages.Chinese,
                                EncodedLoopValidator.RejectionDetail(seam!, Messages.Chinese)) + attempts,
                            ["en"] = reasonEnglish, ["params"] = new JsonArray() };
                        if (sourceHash != await source.SourceHashAsync(cancellationToken)) throw new IOException("Source changed during generation.");
                        await Save();
                        return report;
                    }
                    if (!isStatic && request.ProbeFrames == 0)
                    {
                        progress?.Report(new("checking_hardware_decode", (double)i / groups.Length,
                            "Checking this actual video on the installed hardware decoders."));
                        using (timing.Measure(StageTiming.HardwareDecodeCheck))
                            hardwareDecode = await runner.ProbeHardwareDecodeAsync(video, Path.Combine(work, "hardware-decode"),
                                Math.Min(frames, 5), cancellationToken);
                    }
                    JsonObject layer;
                    using (timing.Measure(StageTiming.ProjectAssembly))
                    layer = await VideoSceneBuilder.WriteLayerAsync(project, id, video,
                        (uint)crop.Width * (packedAlpha && !isStatic ? 2u : 1u), (uint)crop.Height, nextId++, centerX, centerY,
                        crop.Width * captureWidth / pixelWidth, crop.Height * captureHeight / pixelHeight,
                        cancellationToken, packedAlpha, depthX, depthY, x, y, isStatic,
                        capturedColor: HybridScenePlanner.Int(group["parent_id"]) is not null);
                    HybridVideoProjection.AttachToParent(layer, group);
                    replacements[id] = layer;
                    var encodedGroup = new JsonObject {
                        ["id"] = id, ["status"] = "encoded", ["storage"] = isStatic ? "static_rgba" : "video", ["source_layers"] = JsonSerializer.SerializeToNode(layers),
                        ["replacement_layer_id"] = layer["id"]!.DeepClone(), ["packed_alpha"] = packedAlpha,
                        ["crop"] = encoded["crop"]!.DeepClone(), ["video_path"] = video,
                        ["capture_width"] = captureWidth, ["capture_height"] = captureHeight,
                        ["late_dependency_validation"] = lateDependencyValidation,
                        ["encoded_loop_validation"] = seam,
                        ["hardware_decode_preflight"] = encoded["hardware_decode_preflight"]?.DeepClone(),
                        ["hardware_decode"] = hardwareDecode,
                        ["video_bytes"] = new FileInfo(video).Length, ["source_render_passes"] = master["native_result"]?["compiled_scene_passes"]?.DeepClone(),
                        // 成品是渲染那一遍直接编出来的，还是从无损 master 裁切重编的。静态图层两条路线都只存一张 RGBA。
                        ["capture_mode"] = isStatic ? null
                            : directPlayback ? NativeRenderRunner.DirectPlaybackCaptureMode : NativeRenderRunner.LosslessMasterCaptureMode,
                        // 播放版编码的实际档位与耗时；静态图层没有这一段编码，保持为 null。
                        ["playback_encode"] = isStatic ? null : new JsonObject {
                            ["encoder"] = encoded["encoder"]?.DeepClone(),
                            ["encoder_requested"] = encoded["encoder_requested"]?.DeepClone(),
                            ["encoder_used"] = encoded["encoder_used"]?.DeepClone(),
                            ["encoder_fallback_reason"] = encoded["encoder_fallback_reason"]?.DeepClone(),
                            // 直编组这里是 null：编码与渲染重叠，没有可单独横比的秒数，basis 说明这一点。
                            ["encode_seconds"] = encoded["encode_seconds"]?.DeepClone(),
                            ["encode_seconds_basis"] = encoded["encode_seconds_basis"]?.DeepClone(),
                            // mf 档位的硬件 MFT 探测结果，与新增的播放版画质判据。只有走过的档位才有这两段。
                            ["media_foundation"] = encoded["media_foundation"]?.DeepClone(),
                            ["playback_quality_gate"] = encoded["playback_quality_gate"]?.DeepClone(),
                            ["encoder_arguments"] = encoded["encoder_arguments"]?.DeepClone() } };
                    if (groupSeamResidual is not null)
                    {
                        encodedGroup["seam_residual"] = groupSeamResidual.DeepClone();
                        encodedGroup["loop_crossfade"] = groupCrossfade?.DeepClone();
                    }
                    SeamPreview.Attach(encodedGroup, seamPreview);
                    report["groups"]!.AsArray().Add(encodedGroup);
                    await Save();
                }
                catch
                {
                    groupFailed = true;
                    throw;
                }
                finally
                {
                    TemporaryCaptureFiles.Delete(report, masterPath,
                        "preview.mp4", "video.partial.mp4", "first-frame.rgba", "frame-samples.rgb",
                        NativeRenderRunner.RetainedFramesFile, "native/audio.f32le", "native/audio.f32le.partial");
                    try { await Save(); }
                    catch when (groupFailed) { }
                }
            }
            if (!retryNextCandidate && !retryNextStart && !startOffsetRetryPending)
            {
                if (candidateFallbackEnabled)
                {
                    candidateAttempts.Add(LoopCandidateFallback.Attempt(candidateAttempt, loopCandidates[0]!.AsObject(),
                        "encoded", report["groups"]!.AsArray()));
                    if (startFrame != 0) candidateAttempts[^1]!["source_start_frame"] = startFrame;
                }
                selectedCandidateAttempt = candidateAttempt;
                break;
            }
            // 先停掉提前启动、这一轮已经不要的渲染：渲染器还占着组目录时删不掉。
            await ResetGroupRendersAsync();
            // 清掉这一轮所有组的输出目录（master 的中间文件已由每组的 finally 删掉，这里连编码结果一起删），
            // 下一轮从空目录开始，磁盘峰值不叠加。
            foreach (JsonObject group in groups)
            {
                string groupDirectory = ProjectSource.ContainedPath(output, group["id"]!.GetValue<string>());
                if (Directory.Exists(groupDirectory)) Directory.Delete(groupDirectory, recursive: true);
            }
            // 起点偏移重试的是同一个候选：抵消循环尾的 ++，候选序号不变。
            if (startOffsetRetryPending) --candidateAttempt;
            }
            if (candidateFallbackEnabled)
                report["selected_loop_candidate"] = LoopCandidateFallback.Selected(selectedCandidateAttempt, loopCandidates[0]!.AsObject());
            JsonArray finalObjects;
            using (timing.Measure(StageTiming.ProjectAssembly))
            {
            finalObjects = AssembleObjects(originalObjects, plan, replacements, finalDependencies);
            // 记下按"不绘制但带脚本"规则额外保留的根对象，事后核对用。
            report["retained_script_root_ids"] = JsonSerializer.SerializeToNode(ScriptRootIds(originalObjects, plan));
            scene["objects"] = finalObjects;
            HybridScenePlanner.ApplyTextEffectChoice(scene, plan);
            ApplyVisibilityFallbacks(scene, snapshot);
            if (replacements.Count == 0) throw new InvalidDataException("No video group produced visible output; this is not a hybrid candidate.");
            await File.WriteAllTextAsync(ProjectSource.ContainedPath(project, source.SceneResource), scene.ToJsonString(), cancellationToken);
            metadata["title"] = (metadata["title"]?.GetValue<string>() ?? "Wallpaper") + " · Video + live";
            metadata.Remove("workshopid"); metadata.Remove("publishedfileid");
            metadata["type"] = "scene"; metadata["file"] = source.SceneResource;
            if (metadata["general"]?["properties"] is JsonObject exportedProperties)
            {
                // Display conditions hide controls without removing the values read by live scripts.
                foreach (var property in exportedProperties.Select(p => p.Value).OfType<JsonObject>()) property["condition"] = "false";
                string noticeKey = "wpebakersnapshotnotice";
                while (exportedProperties.ContainsKey(noticeKey)) noticeKey += "0";
                exportedProperties[noticeKey] = new JsonObject { ["type"] = "text", ["value"] = "", ["order"] = -1, ["index"] = -1,
                    ["text"] = "画面设置已固定；在 WPE Baker 中改设置后重新生成。 / Settings are fixed; change them in WPE Baker and generate again." };
            }
            metadata["description"] = (metadata["description"]?.GetValue<string>() ?? "") +
                "\nGenerated for the selected settings. Change omitted styles or baked visual settings in WPE Baker and generate again.";
            await File.WriteAllTextAsync(Path.Combine(project, "project.json"), metadata.ToJsonString(), cancellationToken);
            }
            if (sourceHash != await source.SourceHashAsync(cancellationToken)) throw new IOException("Source changed during generation.");
            JsonObject[] encodedGroups = report["groups"]!.AsArray().OfType<JsonObject>()
                .Where(g => g["status"]?.GetValue<string>() == "encoded").ToArray();
            bool seamsPass = request.ProbeFrames == 0 && encodedGroups.All(g =>
                g["encoded_loop_validation"]?["status"]?.GetValue<string>() == "observed_seam_pass");
            report["video_layers"] = replacements.Count - staticLayers;
            report["static_layers"] = staticLayers;
            // 1 帧、0 个视频层的结果单列 static_only：candidate_generated 只给真的含视频循环的成品。
            report["status"] = request.ProbeFrames > 0 ? "probe_generated"
                : !seamsPass ? "candidate_rejected_seam"
                : StaticOnlyBake.Is(report) ? StaticOnlyBake.Status : "candidate_generated";
            report["loop_validation"] = request.ProbeFrames > 0 ? "not_performed" : seamsPass ? "encoded_seams_passed" : "encoded_seam_failed";
            report["project_path"] = project;
            // 播放版编码的汇总：请求档位、实际档位、回退理由与编码总秒数，方便直接和软件档位对比。
            report["playback_encoder"] = PlaybackEncoderSelection.Summarize(request.PlaybackEncoder,
                report["groups"]!.AsArray().OfType<JsonObject>());
            report["retained_object_count"] = finalObjects.Count - replacements.Count;
            report["source_draw_objects_removed"] = groups.Sum(g => g["layer_ids"]!.AsArray().Count);
            if (destination is not null && StaticOnlyBake.Finished(report["status"]!.GetValue<string>()))
            {
                progress?.Report(new("saving_project", 1, "Saving the validated project to the selected wallpaper folder."));
                using (timing.Measure(StageTiming.ProjectAssembly))
                {
                    using var generated = new ProjectSource(project);
                    await generated.ExtractAsync(destination, cancellationToken);
                }
                report["project_path"] = destination;
                report["work_directory"] = output;
                await Save();
                await VideoSceneBuilder.WriteJsonAsync(Path.Combine(destination, "bake.json"), report, cancellationToken);
            }
            await Save();
            return report;
        }
        catch (Exception error)
        {
            report["status"] = cancellationToken.IsCancellationRequested ? "cancelled" : "failed";
            report["error_type"] = error.GetType().Name; report["error"] = error.Message;
            await Save(); throw;
        }
        finally
        {
            // 任何出口（拒绝、异常、取消）都不能把提前启动的渲染器进程留在后面。
            await ResetGroupRendersAsync();
            groupRenderCancellation.Dispose();
        }
    }

    /// <summary>
    /// 主渲染前按 composition probe 的试编码外推每个视频组的成品大小（EmbeddedVideoBudget.EvaluateProbe）。
    /// 读不到试编码时记 not_estimated、不拒绝：编码后还有一次按实际字节的检查。
    /// </summary>
    private async Task<JsonObject> EstimateEmbeddedVideoAsync(JsonObject compositionValidation, ulong frames,
        HybridAnalyzeRequest settings, CancellationToken cancellationToken)
    {
        var samples = new List<EmbeddedVideoBudget.ProbeGroup>();
        string? reason = null;
        try
        {
            string probeOutput = compositionValidation["probe_output_path"]?.GetValue<string>()
                ?? throw new InvalidDataException("The composition validation omitted its probe output path.");
            JsonObject probe = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(probeOutput, "bake.json"), cancellationToken))?.AsObject()
                ?? throw new InvalidDataException("The composition probe report is empty.");
            ulong probeFrames = probe["frames"]?.GetValue<ulong>() ?? 0;
            foreach (JsonObject group in probe["groups"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                if (group["storage"]?.GetValue<string>() != "video" || group["video_path"]?.GetValue<string>() is not string video || !File.Exists(video))
                    continue;
                bool packed = group["packed_alpha"]?.GetValue<bool>() == true;
                uint width = group["crop"]?["width"]?.GetValue<uint>() ?? 0, height = group["crop"]?["height"]?.GetValue<uint>() ?? 0;
                samples.Add(new(group["id"]!.GetValue<string>(), packed, width * (packed ? 2u : 1u), height, probeFrames,
                    new FileInfo(video).Length, await EmbeddedVideoBudget.ReadPacketsAsync(tools, video, cancellationToken)));
            }
            if (samples.Count == 0) reason = "The composition probe stored no encoded video group (every group was static).";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or InvalidOperationException or FormatException or
            System.ComponentModel.Win32Exception)
        {
            samples.Clear();
            reason = "The composition probe encode could not be read: " + error.Message;
        }
        return EmbeddedVideoBudget.EvaluateProbe(samples, frames, settings.FpsNumerator, settings.FpsDenominator, reason);
    }

    private async Task<JsonObject> ValidateCompositionAsync(HybridBakeRequest request, JsonObject plan,
        HybridAnalyzeRequest settings, ProjectSource source, string output, IProgress<RenderProgress>? progress,
        CancellationToken cancellationToken)
    {
        string probeOutput = output + ".composition-probe";
        EnsureNewDerivedOutput(source, probeOutput, "Composition probe output");
        string? selectedDevice = request.DeviceUuid ?? settings.DeviceUuid;
        progress?.Report(new("checking_composition", 0, "Generating a short candidate to check complete scene composition."));
        JsonObject probe = await BakeAsync(new(2, plan, probeOutput, HybridCompositionValidator.RequiredFrames,
            selectedDevice), progress, cancellationToken);
        return await ValidateProbeCompositionAsync(plan, probe, output, progress, cancellationToken);
    }

    internal async Task<JsonObject> ValidateProbeCompositionAsync(JsonObject plan, JsonObject probe,
        string outputPrefix, IProgress<RenderProgress>? progress = null, CancellationToken cancellationToken = default,
        JsonObject? input = null, JsonArray? inputTimeline = null)
    {
        var settings = plan["settings"]?.Deserialize<HybridAnalyzeRequest>(JsonOptions)
            ?? throw new InvalidDataException("Invalid capture settings.");
        using var source = new ProjectSource(plan["source"]?.GetValue<string>()
            ?? throw new InvalidDataException("Hybrid plan source is missing."));
        if (await source.SourceHashAsync(cancellationToken) != plan["source_sha256"]?.GetValue<string>())
            throw new InvalidDataException("Source changed; analyze it again.");
        string comparisonReference = outputPrefix + ".composition-reference";
        string comparisonOutput = outputPrefix + ".composition-validation";
        EnsureNewDerivedOutput(source, comparisonReference, "Composition reference output");
        EnsureNewDerivedOutput(source, comparisonOutput, "Composition validation output");
        if (probe["status"]?.GetValue<string>() != "probe_generated")
            throw new InvalidDataException("The short composition probe did not produce a project for paired comparison.");
        string project = Path.GetFullPath(probe["project_path"]?.GetValue<string>()
            ?? throw new InvalidDataException("The short composition probe omitted its project path."));
        string probeOutput = Path.GetDirectoryName(project)
            ?? throw new InvalidDataException("The short composition probe project path has no parent directory.");
        string captureSource = Path.Combine(probeOutput, "capture-source");
        await CreateCompositionReferenceAsync(source, comparisonReference, plan["snapshot_properties"]!.AsObject(),
            settings.ViewMode, plan, cancellationToken);
        var comparison = await new CandidateValidation(tools).ValidateAsync(new ValidationRequest(
            1, comparisonReference, project, settings.Assets, comparisonOutput, settings.Width, settings.Height,
            settings.FpsNumerator, settings.FpsDenominator, HybridCompositionValidator.RequiredFrames,
            WarmupFrames: 0, Seed: 17, DeviceUuid: settings.DeviceUuid,
            UserProperties: plan["snapshot_properties"]!.DeepClone().AsObject(),
            Input: input ?? new JsonObject { ["cursor_x"] = .5, ["cursor_y"] = .5, ["cursor_in_window"] = true },
            InputTimeline: inputTimeline,
            TileSize: HybridCompositionValidator.RequiredTileSize, RetainRawFrames: false), progress, cancellationToken);
        JsonObject validation = HybridCompositionValidator.Evaluate(comparison);
        validation["probe_output_path"] = probeOutput;
        validation["probe_capture_source_path"] = captureSource;
        validation["comparison_reference_path"] = comparisonReference;
        validation["probe_project_path"] = project;
        validation["comparison_output_path"] = comparisonOutput;
        validation["occlusion_tradeoff"] = plan["occlusion_tradeoff"]?.DeepClone();
        validation["text_effects_choice"] = plan["text_effects_choice"]?.DeepClone();
        validation["audio_effects_choice"] = plan["audio_effects_choice"]?.DeepClone();
        return validation;
    }

    private static async Task CreateCompositionReferenceAsync(ProjectSource source, string destination,
        JsonObject snapshot, string viewMode, JsonObject plan, CancellationToken cancellationToken)
    {
        JsonObject scene = source.ReadJson(source.SceneResource);
        JsonObject metadata = source.Contains("project.json") ? source.ReadJson("project.json") : new JsonObject();
        HybridScenePlanner.ApplyAudioEffectChoice(scene, plan);
        if (viewMode == "fixed_view") scene["general"]!["cameraparallax"] = false;
        HybridScenePlanner.ApplyOverlayPlacement(scene, plan);
        HybridScenePlanner.ApplyTextEffectChoice(scene, plan);
        ApplyPropertySnapshot(metadata, snapshot);
        metadata["file"] = source.SceneResource;
        await source.ExtractAsync(destination, cancellationToken);
        await File.WriteAllTextAsync(ProjectSource.ContainedPath(destination, source.SceneResource),
            scene.ToJsonString(), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(destination, "project.json"), metadata.ToJsonString(), cancellationToken);
    }

    private static void EnsureNewDerivedOutput(ProjectSource source, string destination, string description)
    {
        destination = Path.GetFullPath(destination);
        ProjectSource.EnsureNoReparsePoints(destination);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException($"{description} must be new.");
        string sourcePrefix = Path.TrimEndingDirectorySeparator(source.DirectoryPath) + Path.DirectorySeparatorChar;
        if (destination.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"{description} must be outside the source project.");
    }


    internal static void ApplyPropertySnapshot(JsonObject project, JsonObject snapshot)
    {
        if (project["general"]?["properties"] is not JsonObject properties) return;
        foreach (var (key, value) in snapshot)
            if (properties[key] is JsonObject property) property["value"] = value?.DeepClone();
    }

    internal static JsonArray? SelectVideoRateOverrides(JsonObject loop, IReadOnlySet<int> groupLayerIds)
    {
        JsonObject? candidate = loop["candidates"]?.AsArray().FirstOrDefault()?.AsObject();
        if (candidate is null) return null;
        var owners = new HashSet<int>();
        var overrides = new JsonArray();
        foreach (JsonObject patch in candidate["patches"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            if (patch["kind"]?.GetValue<string>() != "video_rate") continue;
            int? owner = HybridScenePlanner.Int(patch["owner_layer_id"]);
            if (owner is null || !groupLayerIds.Contains(owner.Value)) continue;
            if (!owners.Add(owner.Value) || patch["rate_numerator"] is null || patch["rate_denominator"] is null)
                throw new InvalidDataException("Video rate patches must provide one exact override per captured owner.");
            overrides.Add(new JsonObject { ["owner_layer_id"] = owner.Value,
                ["rate_numerator"] = patch["rate_numerator"]!.DeepClone(),
                ["rate_denominator"] = patch["rate_denominator"]!.DeepClone() });
        }
        return overrides.Count == 0 ? null : overrides;
    }

    internal static void ApplyVisibilityFallbacks(JsonObject scene, JsonObject snapshot)
    {
        // The player can instantiate visibility before applying properties. Only synchronize
        // boolean fallbacks: a scalar slider must not replace a serialized vector such as scale.
        foreach (var binding in SceneAnalyzer.Walk(scene).OfType<JsonObject>().Where(n => n.ContainsKey("user") &&
            n["value"] is JsonValue value && value.TryGetValue<bool>(out _)).ToArray())
            binding["value"] = HybridScenePlanner.Resolve(binding, snapshot);
    }
}
