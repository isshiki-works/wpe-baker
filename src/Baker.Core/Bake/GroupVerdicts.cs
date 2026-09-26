using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 逐组判定：组主渲染之后按固定顺序过的几道判定，各自给出写进 bake.json 的组记录；拒绝的那几道同时把整案状态与理由写进报告。
/// 顺序（由 <see cref="HybridBakeService"/> 的组循环保证）：晚期外部依赖 → 空组 → 残差第一层 → 静态证明 → 内嵌视频实际字节 →
/// 接缝（闭合检验 + 编码后参照式校验）→ 成品记录。
/// </summary>
internal static class GroupVerdicts
{
    private static JsonNode? Layers(int[] layers) => JsonSerializer.SerializeToNode(layers);

    /// <summary>从源第 0 帧到捕获区间末尾观察到的晚期外部依赖与源脚本报错（有界证据，不是对未执行分支的证明）。</summary>
    internal static JsonObject LateDependency(string id, int[] layers, JsonArray lateDependencies, JsonArray unsafeScriptErrors,
        JsonArray externalLiveScriptErrors, ulong renderedFrames, ulong warmupFrames, string masterPath) => new()
    {
        ["status"] = lateDependencies.Count == 0 && unsafeScriptErrors.Count == 0 ? "observed_no_late_external_dependency" : "rejected",
        ["group_id"] = id,
        ["source_layers"] = Layers(layers),
        ["full_capture_frames"] = renderedFrames,
        ["warmup_frames"] = warmupFrames,
        ["capture_manifest"] = Path.Combine(masterPath, "manifest.json"),
        ["dependencies"] = lateDependencies.DeepClone(),
        ["source_script_errors"] = unsafeScriptErrors.DeepClone(),
        ["external_live_source_script_errors"] = externalLiveScriptErrors.DeepClone(),
        ["scope"] = "Observed from source frame zero through the captured interval. Reject wall-clock, external-audio, pointer or media input to this group or its retained ancestors. Reject non-initialization writes from outside the group into its layers or ancestors, writes from a group owner to any known source object outside the group, and source script faults in baked content, retained ancestors or layers not already retained live. Faults in an external retained-live owner remain isolated there and are reported. This is bounded dependency evidence, not a proof about unexecuted later branches."
    };

    /// <summary>有晚期外部依赖或不安全的源脚本报错时拒绝整案（true）：脚本报错优先，其次跨边界写入，其余是外部输入。</summary>
    internal static bool RejectLateDependency(JsonObject report, string id, int[] layers, JsonObject validation)
    {
        JsonArray dependencies = validation["dependencies"]!.AsArray(), unsafeScriptErrors = validation["source_script_errors"]!.AsArray();
        if (dependencies.Count == 0 && unsafeScriptErrors.Count == 0) return false;
        bool crossBoundaryWrite = dependencies.OfType<JsonObject>().Any(dependency => dependency["operation"]?.GetValue<string>() == "write");
        report["groups"]!.AsArray().Add(new JsonObject
        {
            ["id"] = id,
            ["status"] = unsafeScriptErrors.Count > 0 ? "rejected_late_source_script_error" :
                crossBoundaryWrite ? "rejected_late_external_write" : "rejected_late_external_input",
            ["source_layers"] = Layers(layers),
            ["late_dependency_validation"] = validation.DeepClone()
        });
        report["status"] = "candidate_rejected_late_dependency";
        new Message(unsafeScriptErrors.Count > 0 ? "bake.late_script_fault"
            : crossBoundaryWrite ? "bake.late_external_write" : "bake.late_external_input").Write(report, "reason");
        report["late_dependency_validation"] = validation;
        report["loop_validation"] = "not_performed";
        return true;
    }

    /// <summary>整段区间里一个像素都没有：不出视频，记一条空组。</summary>
    internal static JsonObject Empty(string id, int[] layers, JsonObject? lateDependency, JsonObject master) => new()
    {
        ["id"] = id, ["status"] = "empty_in_generated_interval",
        ["source_layers"] = Layers(layers), ["late_dependency_validation"] = lateDependency,
        ["gpu_bounds_prepass"] = master["gpu_bounds_prepass"]?.DeepClone()
    };

    /// <summary>
    /// 起点搜索里没有一个相位过整幅 Δ_0 限（整幅均值在降采样样本上与全分辨率几乎相同，语料里差不到 0.01/255），全分辨率复核必拒。
    /// 超限的组里有被掩盖的粒子时，拒绝后反正要留粒子实时重烘（<see cref="HybridBakeService.ResidualParticleRoots"/>），
    /// 返回这些组的拒绝记录，不再渲 master；其余情况 null，照旧渲染并在全分辨率复核。
    /// </summary>
    internal static JsonArray? StartSearchRejection(JsonObject plan, JsonObject report, JsonObject startSearch, Func<string, int[]> layersOf)
    {
        if (startSearch["candidates_within_global_limit"]?.GetValue<int>() != 0) return null;
        IEnumerable<(string Id, JsonNode Row)> rows = startSearch["groups"] is JsonArray groups
            ? groups.OfType<JsonObject>().Select(g => (g["group_id"]!.GetValue<string>(), g["at_shared_start"]!))
            : [(startSearch["group_id"]!.GetValue<string>(), startSearch["selected"]!)];
        var rejected = new JsonArray([.. rows.Where(r => r.Row["sampled_global_rgb_mae_255"]!.GetValue<double>() > ResidualMasking.MaximumSeamRgbMae255)
            .Select(r => (JsonNode)new JsonObject { ["id"] = r.Id, ["status"] = "rejected_seam_residual", ["storage"] = "video",
                ["source_layers"] = Layers(layersOf(r.Id)), ["start_search_at_selected"] = r.Row.DeepClone() })]);
        var verdict = new JsonObject { ["status"] = "candidate_rejected_seam", ["loop_validation"] = "residual_above_limits",
            ["groups"] = rejected, ["residual_masking"] = report["residual_masking"]?.DeepClone() };
        return HybridBakeService.ResidualParticleRoots(plan, verdict) is null ? null : rejected;
    }

    /// <summary>写出起点搜索就判定的残差拒绝（<see cref="StartSearchRejection"/>）。</summary>
    internal static void RejectStartSearch(JsonObject report, JsonArray rejected)
    {
        foreach (JsonNode? group in rejected) report["groups"]!.AsArray().Add(group!.DeepClone());
        report["status"] = "candidate_rejected_seam";
        report["loop_validation"] = "residual_above_limits";
        string whole = ResidualMasking.MaximumSeamRgbMae255.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        string readings = string.Join(", ", rejected.OfType<JsonObject>().Select(group =>
            $"{group["id"]} {group["start_search_at_selected"]!["sampled_global_rgb_mae_255"]}/255"));
        new Message("bake.residual_start_search_rejected", [readings, whole], [readings, whole]).Write(report, "reason");
    }

    /// <summary>选定起点在全分辨率第一层被拒：记本次各组的残差与起点尝试，不启动整案重渲。</summary>
    internal static void RejectResidual(JsonObject report, string id, int[] layers, JsonObject? lateDependency, JsonObject seamResidual,
        JsonObject preview, JsonArray startAttempts, int candidateCount, uint crossfadeFrames)
    {
        var rejected = new JsonObject {
            ["id"] = id, ["status"] = "rejected_seam_residual", ["storage"] = "video",
            ["source_layers"] = Layers(layers),
            ["late_dependency_validation"] = lateDependency,
            ["seam_residual"] = seamResidual.DeepClone() };
        SeamPreview.Attach(rejected, preview);
        report["groups"]!.AsArray().Add(rejected);
        report["status"] = "candidate_rejected_seam";
        report["loop_validation"] = "residual_above_limits";
        LoopStartSelector.RejectionReason(startAttempts, candidateCount, crossfadeFrames).Write(report, "reason");
    }

    /// <summary>计划证明这个组源与运行时都静态，渲出来却有变化：证明失效，拒绝整案（true）。</summary>
    internal static bool RejectStaticProof(JsonObject report, JsonObject group, string id, int[] layers, JsonObject? lateDependency, bool isStatic)
    {
        if (!(group["static_verified"]?.GetValue<bool>() == true &&
            group["static_verification"]?["basis"]?.GetValue<string>() == "source_and_runtime_static_proof" && !isStatic)) return false;
        report["groups"]!.AsArray().Add(new JsonObject { ["id"] = id, ["status"] = "rejected_static_proof",
            ["source_layers"] = Layers(layers), ["late_dependency_validation"] = lateDependency });
        report["status"] = "candidate_rejected_static_proof";
        new Message("bake.static_proof_changed").Write(report, "reason");
        return true;
    }

    /// <summary>
    /// 实际字节已超内嵌视频上限（WPE 放不出来），或 <paramref name="reason"/> 给出的体积限制下画质门未过：接缝与装配都不必再做。
    /// </summary>
    internal static void RejectEmbeddedSize(JsonObject report, string id, int[] layers, bool packedAlpha, JsonObject encoded, string video,
        long encodedBytes, JsonObject? lateDependency, ulong frames, HybridAnalyzeRequest settings, Message? reason = null)
    {
        report["groups"]!.AsArray().Add(new JsonObject {
            ["id"] = id, ["status"] = "rejected_embedded_video_size", ["storage"] = "video",
            ["source_layers"] = Layers(layers), ["packed_alpha"] = packedAlpha,
            ["crop"] = encoded["crop"]!.DeepClone(), ["video_path"] = video,
            ["video_bytes"] = encodedBytes, ["maximum_bytes"] = EmbeddedVideoBudget.MaximumBytes,
            ["late_dependency_validation"] = lateDependency,
            ["encoded_loop_validation"] = null, ["hardware_decode"] = null });
        report["status"] = EmbeddedVideoBudget.RejectedBakeStatus;
        (reason ?? EmbeddedVideoBudgetJson.EncodedRejection(id, encodedBytes, frames, settings.FpsNumerator, settings.FpsDenominator)).Write(report, "reason");
    }

    /// <summary>
    /// 接缝判定。闭合检验在编码前的原帧上做：第 P 帧对第 0 帧。不淡化的组据此裁决；残差组只记录（硬切残差由残差层判）。
    /// 残差路线里不含残差层的组没有残差层替它判，照源周期路线裁决。闭合通过的视频组再做编码后参照式校验。
    /// </summary>
    internal static async Task<JsonObject> SeamAsync(NativeTools tools, JsonObject master, string masterPath, string video, CacheRegion crop,
        GroupCapture capture, bool isStatic, bool packedAlpha, bool directPlayback, bool residualGroup, bool keepIntermediates,
        ulong frames, uint crossfadeFrames, HybridAnalyzeRequest settings, CancellationToken cancellationToken)
    {
        byte[] firstFrame = await LoopClosureCheck.ReadRetainedFrameAsync(master, 0, cancellationToken);
        byte[] wrapFrame = await LoopClosureCheck.ReadRetainedFrameAsync(master, frames, cancellationToken);
        JsonObject closure = LoopClosureCheck.Evaluate(firstFrame, wrapFrame,
            (int)capture.PixelWidth, (int)capture.PixelHeight, withAlpha: !capture.SceneClear, frames, judged: !residualGroup,
            SwayRecurrenceSolver.SpeedLimitScale(settings.Width, settings.Height));
        if (isStatic)
            return new JsonObject { ["status"] = LoopClosureCheck.Allows(closure) ? "observed_seam_pass" : "observed_seam_fail",
                ["failures"] = LoopClosureCheck.Allows(closure) ? new JsonArray() : new JsonArray("loop_not_closed"),
                ["basis"] = "Every encoded RGBA frame was byte-identical in this interval; stored as one static texture. Frame P must still close onto frame 0.",
                ["loop_closure"] = closure };
        if (!keepIntermediates && LoopClosureCheck.Allows(closure))
            return await EncodedLoopValidator.ValidateAsync(video, tools, frames, settings.FpsNumerator,
                settings.FpsDenominator, packedAlpha, closure, crop.Width * (packedAlpha ? 2 : 1), crop.Height,
                cancellationToken: cancellationToken);
        // 直编组没有无损 master 可解：m[0]、m[P−1] 直接用渲染时留下的原帧（就是编码器第 0、P−1 帧的输入）。
        // master 是无损的，从它解出来的那两帧与原帧逐字节相同，所以判据读数与 master 路线一致。
        return await EncodedLoopValidator.ValidateAsync(video, tools, frames, settings.FpsNumerator, settings.FpsDenominator,
            packedAlpha, directPlayback
                ? await EncodedLoopValidator.FromRetainedFramesAsync(master, crop, packedAlpha, frames, closure,
                    wrapFrame, cancellationToken)
                : await EncodedLoopValidator.FromLosslessMasterAsync(tools, masterPath, crop, packedAlpha, frames,
                    residualGroup ? crossfadeFrames : 0, closure, wrapFrame, cancellationToken), cancellationToken: cancellationToken);
    }

    /// <summary>探针里整段逐字节不变的组：没有闭合可判，直接记通过。</summary>
    internal static JsonObject ProbeStatic() =>
        new() { ["status"] = "observed_seam_pass", ["basis"] = "Every captured RGBA frame was byte-identical in this probe interval; stored as one static texture." };

    /// <summary>
    /// 选中候选（plan 首个循环候选）的缓变分量（slow_components：周期远超循环上限、不进求解器）里最大的漂移上界（换算成度）与所有者层；
    /// 给了 <paramref name="layers"/> 就只看所有者层在其中的。没有时 null。接缝门照常在 P 处判，它的结果就是结论，这里只决定结论怎么写、退回哪些层。
    /// </summary>
    internal static (string Degrees, int[] Owners)? SlowDrift(JsonObject plan, int[]? layers = null)
    {
        JsonObject[] slow = [.. ((plan["loop"]?["candidates"] as JsonArray)?.FirstOrDefault()?["slow_components"] as JsonArray ?? [])
            .OfType<JsonObject>().Where(component => layers is null || SceneGraph.Int(component["owner_layer_id"]) is int owner && layers.Contains(owner))];
        return slow.Length == 0 ? null : ((slow.Max(component => component["drift_bound_radians"]!.GetValue<double>()) * 180 / Math.PI)
            .ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
            [.. slow.Select(component => SceneGraph.Int(component["owner_layer_id"])).OfType<int>().Distinct()]);
    }

    /// <summary>
    /// 成品组的接缝没过：记组记录与中英理由，拒绝整案。<paramref name="slowDrift"/>（<see cref="SlowDrift"/>）非空且没过的是
    /// 循环闭合检查时，理由写缓变分量漂移（reason.slow_component_drift_exceeds_seam），附漂移上界与接缝读数，返回 true
    /// （烘焙外层据此把这些层退回实时再烘，没得退时这就是"不能"的结论）。
    /// </summary>
    internal static bool RejectSeam(JsonObject report, string id, int[] layers, bool packedAlpha, JsonObject encoded, string video,
        JsonObject? lateDependency, JsonObject? seam, JsonObject? preview, string? slowDrift)
    {
        var rejected = new JsonObject {
            ["id"] = id, ["status"] = "rejected_seam", ["storage"] = "video",
            ["source_layers"] = Layers(layers), ["packed_alpha"] = packedAlpha,
            ["crop"] = encoded["crop"]!.DeepClone(), ["video_path"] = video,
            ["late_dependency_validation"] = lateDependency,
            ["encoded_loop_validation"] = seam,
            ["hardware_decode"] = null };
        SeamPreview.Attach(rejected, preview);
        report["groups"]!.AsArray().Add(rejected);
        report["status"] = "candidate_rejected_seam";
        report["loop_validation"] = "encoded_seam_failed";
        if (slowDrift is not null && seam?["loop_closure"]?["status"]?.GetValue<string>() == LoopClosureCheck.NotClosedStatus)
        {
            new Message("reason.slow_component_drift_exceeds_seam",
                [slowDrift, EncodedLoopValidator.RejectionDetail(seam, MessageCatalog.English)],
                [slowDrift, EncodedLoopValidator.RejectionDetail(seam, MessageCatalog.Chinese)]).Write(report, "reason");
            return true;
        }
        string reasonEnglish = MessageCatalog.Get("bake.encoded_seam_rejected", MessageCatalog.English,
            EncodedLoopValidator.RejectionDetail(seam!, MessageCatalog.English));
        report["reason"] = reasonEnglish;
        report["reason_localized"] = new JsonObject { ["key"] = "bake.encoded_seam_rejected",
            ["zh"] = MessageCatalog.Get("bake.encoded_seam_rejected", MessageCatalog.Chinese,
                EncodedLoopValidator.RejectionDetail(seam!, MessageCatalog.Chinese)),
            ["en"] = reasonEnglish, ["params"] = new JsonArray() };
        return false;
    }

    /// <summary>
    /// 成品组记录：替换图层、裁剪、编码档位与耗时、接缝与硬解结果；残差组另带第一层读数与淡化记录。
    /// capture_mode 记成品是渲染那一遍直接编出来的，还是从无损 master 裁切重编的；静态图层两条路线都只存一张 RGBA。
    /// </summary>
    internal static JsonObject Encoded(string id, int[] layers, JsonObject layer, bool isStatic, bool packedAlpha, JsonObject encoded,
        string video, GroupCapture capture, JsonObject? lateDependency, JsonObject? seam, JsonObject? hardwareDecode, JsonObject master,
        bool gpuDirect, bool directPlayback, JsonObject? seamResidual, JsonObject? crossfade, JsonObject? preview)
    {
        var record = new JsonObject {
            ["id"] = id, ["status"] = "encoded", ["storage"] = isStatic ? "static_rgba" : "video", ["source_layers"] = Layers(layers),
            ["replacement_layer_id"] = layer["id"]!.DeepClone(), ["packed_alpha"] = packedAlpha,
            ["crop"] = encoded["crop"]!.DeepClone(), ["video_path"] = video,
            ["capture_width"] = capture.Width, ["capture_height"] = capture.Height,
            ["late_dependency_validation"] = lateDependency,
            ["encoded_loop_validation"] = seam,
            ["hardware_decode_preflight"] = encoded["hardware_decode_preflight"]?.DeepClone(),
            ["hardware_decode"] = hardwareDecode,
            ["video_bytes"] = new FileInfo(video).Length, ["source_render_passes"] = master["native_result"]?["compiled_scene_passes"]?.DeepClone(),
            ["gpu_bounds_prepass"] = master["gpu_bounds_prepass"]?.DeepClone(),
            ["coverage_rerender"] = master["coverage_rerender"]?.DeepClone(),
            ["capture_mode"] = isStatic ? null
                : gpuDirect ? "gpu_direct_playback" : directPlayback ? NativeRenderRunner.DirectPlaybackCaptureMode : NativeRenderRunner.LosslessMasterCaptureMode,
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
                ["playback_quality_gate"] = encoded["playback_quality_gate"]?.DeepClone(),
                ["encoder_arguments"] = encoded["encoder_arguments"]?.DeepClone() } };
        if (seamResidual is not null)
        {
            record["seam_residual"] = seamResidual.DeepClone();
            record["loop_crossfade"] = crossfade?.DeepClone();
        }
        SeamPreview.Attach(record, preview);
        return record;
    }
}
