using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>Builds independently periodic effect-prefix caches while retaining the authored owner and suffix effects.</summary>
internal sealed class EffectPrefixBakeService(NativeTools tools)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    internal static void ValidateSource(ProjectSource source, JsonObject scene, JsonObject cache)
    {
        int owner = cache["owner_layer_id"]?.GetValue<int>() ?? throw new InvalidDataException("Effect-prefix cache owner_layer_id is missing.");
        int prefix = cache["prefix_effect_count"]?.GetValue<int>() ?? 0;
        JsonObject node = scene["objects"]?.AsArray().OfType<JsonObject>().SingleOrDefault(x => SceneGraph.Id(x) == owner)
            ?? throw new InvalidDataException("Effect-prefix cache owner is absent from the source scene.");
        if (prefix <= 0 || node["effects"] is not JsonArray effects || prefix > effects.Count ||
            effects[prefix - 1]?["id"]?.GetValue<int>() != cache["terminal_effect_id"]?.GetValue<int>() ||
            node["image"]?.GetValue<string>() != cache["source_image"]?.GetValue<string>() ||
            cache["loop"] is not JsonObject || cache["fixed_user_properties"] is not JsonObject)
            throw new InvalidDataException("Effect-prefix cache description no longer matches its source owner, terminal effect, or fixed properties.");
        if (!source.Contains(node["image"]!.GetValue<string>())) throw new InvalidDataException("Effect-prefix cache owner image must be project-owned.");
        EffectPrefixCache.ValidateSource(source, scene, scene, owner, prefix);
    }

    internal static async Task<JsonArray> PrepareCaptureSourceAsync(string captureProject, ProjectSource source,
        string? assets, JsonObject pristine, JsonObject snapshot, JsonObject loop, CancellationToken cancellationToken)
    {
        JsonObject scene = pristine.DeepClone().AsObject();
        PlanTransforms.FreezeTemporalProperties(scene, snapshot);
        HybridLoopService.ApplyPatches(scene, loop);
        await source.ExtractAsync(captureProject, cancellationToken);
        await File.WriteAllTextAsync(ProjectSource.ContainedPath(captureProject, source.SceneResource), scene.ToJsonString(), cancellationToken);
        // The loop includes the retimed sway coefficients, not just the scene's speed constants.
        // Keep these overrides in the capture copy: the candidate's retained suffix and the
        // pristine composition reference must continue to use the authored shaders.
        JsonArray patches = await ShaderTextPatch.WriteSwayRetimeAsync(captureProject, source, assets, loop, cancellationToken);
        JsonObject metadata = source.Contains("project.json") ? source.ReadJson("project.json") : new JsonObject();
        ProjectWriter.ApplyPropertySnapshot(metadata, snapshot); metadata["file"] = source.SceneResource;
        await File.WriteAllTextAsync(Path.Combine(captureProject, "project.json"), metadata.ToJsonString(), cancellationToken);
        return patches;
    }

    internal async Task<JsonObject> BakeAsync(HybridBakeRequest request, IProgress<RenderProgress>? progress,
        StageTiming timing, CancellationToken cancellationToken = default)
    {
        if (request.SchemaVersion != 2 || request.ProbeFrames != 0) throw new InvalidDataException("Effect-prefix baking requires a production version 2 request.");
        JsonObject plan = request.Plan.DeepClone().AsObject();
        if (plan["effect_prefix_caches"] is not JsonArray { Count: > 0 } caches)
            throw new InvalidDataException("The plan has no effect_prefix_caches.");
        HybridAnalyzeRequest settings = PlanSettings.Of(plan);
        using var source = new ProjectSource(plan["source"]?.GetValue<string>() ?? throw new InvalidDataException("Effect-prefix source is missing."));
        string hash = await source.SourceHashAsync(cancellationToken);
        if (hash != plan["source_sha256"]?.GetValue<string>()) throw new InvalidDataException("Source changed; analyze it again.");
        string output = Path.GetFullPath(request.OutputDirectory);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Effect-prefix output must be new.");
        string runtimePath = plan["runtime_evidence"]?.GetValue<string>() ?? throw new InvalidDataException("Runtime evidence is missing.");
        JsonObject runtime = JsonNode.Parse(await File.ReadAllTextAsync(runtimePath, cancellationToken))?.AsObject()
            ?? throw new InvalidDataException("Runtime evidence is invalid.");
        JsonObject pristine = source.ReadJson(source.SceneResource);
        JsonObject snapshot = plan["snapshot_properties"]?.AsObject() ?? throw new InvalidDataException("Effect-prefix snapshot properties are missing.");
        // 前缀循环在 bake 侧按同一个入口重算，投影记录取 plan 里那一份（与 PlanTransforms.RefreshLoop 同样的取法）：
        // 缓存描述要和 analyze 逐字节相同，档位口径一有出入这里就会判成 stale。
        JsonObject projection = plan["projection"] as JsonObject ?? new JsonObject();
        JsonArray proposed = EffectPrefixPlanner.Propose(pristine, source, settings.Assets, runtime, snapshot, settings, projection);
        if (caches.Any(cache => cache is not JsonObject) ||
            caches.OfType<JsonObject>().Select(cache => cache["owner_layer_id"]?.GetValue<int>()).Distinct().Count() != caches.Count ||
            caches.OfType<JsonObject>().Any(cache => !proposed.Any(candidate => JsonNode.DeepEquals(candidate, cache))))
            throw new InvalidDataException("Effect-prefix cache descriptions are stale or manually changed; analyze again.");
        foreach (JsonObject cache in caches.OfType<JsonObject>())
        {
            ValidateSource(source, pristine, cache);
        }
        Directory.CreateDirectory(output);
        string candidateProject = Path.Combine(output, "project"), referenceProject = Path.Combine(output, "reference");
        using (timing.Measure(StageTiming.SourceCapture))
        {
            await source.ExtractAsync(candidateProject, cancellationToken);
            await source.ExtractAsync(referenceProject, cancellationToken);
        }
        JsonObject candidateScene = pristine.DeepClone().AsObject();
        JsonObject referenceScene = pristine.DeepClone().AsObject();
        JsonObject candidateMetadata = source.Contains("project.json") ? source.ReadJson("project.json") : new JsonObject();
        JsonObject referenceMetadata = candidateMetadata.DeepClone().AsObject();
        HashSet<string> cachedPropertyKeys = EffectPrefixCache.FixedPropertyKeys(caches.OfType<JsonObject>());
        JsonObject result = BakeReportWriter.EffectPrefixRunning(hash, plan.DeepClone().AsObject());
        string reportPath = Path.Combine(output, "bake.json");
        async Task Save()
        {
            timing.Stamp(result);
            result["playback_encoder"] = PlaybackEncoderSelection.Summarize(request.PlaybackEncoder,
                result["groups"]!.AsArray().OfType<JsonObject>());
            await File.WriteAllTextAsync(reportPath, result.ToJsonString(JsonOptions), CancellationToken.None);
        }
        await Save();
        try
        {
            var runner = new NativeRenderRunner(tools);
            (string playbackKind, string? playbackFallback) =
                await runner.ResolvePlaybackEncoderAsync(request.PlaybackEncoder, output, cancellationToken);
            foreach (JsonObject cache in caches.OfType<JsonObject>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                int owner = cache["owner_layer_id"]!.GetValue<int>(), prefix = cache["prefix_effect_count"]!.GetValue<int>();
                JsonObject loop = EffectPrefixPlanner.AnalyzeIndexedPrefix(pristine, source, settings.Assets, runtime, snapshot,
                    owner, prefix, settings, projection, null);
                if (loop["unresolved"] is JsonArray { Count: > 0 } || loop["candidates"] is not JsonArray { Count: > 0 })
                    throw new InvalidDataException($"Effect-prefix owner {owner} has no complete source-derived period.");
                ulong frames = loop["candidates"]!.AsArray()[0]!["frames"]!.GetValue<ulong>();
                string cacheOutput = Path.Combine(output, "prefix-" + owner);
                string captureProject = Path.Combine(cacheOutput, "capture-source");
                JsonArray swayPatches;
                using (timing.Measure(StageTiming.SourceCapture))
                    swayPatches = await PrepareCaptureSourceAsync(captureProject, source, settings.Assets,
                        pristine, snapshot, loop, cancellationToken);
                if (swayPatches.Count > 0)
                {
                    JsonArray recorded = (result["sway_shader_patches"] ??= new JsonArray()).AsArray();
                    foreach (JsonObject patch in swayPatches.OfType<JsonObject>())
                    {
                        patch["owner_layer_id"] = owner;
                        recorded.Add(patch.DeepClone());
                    }
                }
                var target = new RenderCaptureSelection(owner, cache["terminal_effect_id"]!.GetValue<int>(), EffectTerminal: true,
                    ExactExtent: true, ForceVisibleOwner: cache["preserve_external_visibility"]?.GetValue<bool>() == true ? true : null);
                JsonObject probe;
                using (timing.Measure(StageTiming.MasterRender))
                probe = await runner.RenderRawAsync(new(captureProject, settings.Assets, Path.Combine(cacheOutput, "metadata"), 64, 64,
                    settings.FpsNumerator, settings.FpsDenominator, 1, Seed: 17, CaptureTarget: target with { ExactExtent = false },
                    UserProperties: snapshot, DeviceUuid: request.DeviceUuid ?? settings.DeviceUuid, TraceScene: true), cancellationToken);
                JsonObject captureSource = probe["native_result"]?["capture_source"]?.AsObject()
                    ?? throw new InvalidDataException("Terminal metadata probe omitted its capture source.");
                // 尺寸、编码都以捕获点确实是这一层自己的目标为前提；落到共用缓冲时录到的是整幅场景，直接拒绝，不做完整捕获。
                JsonObject captureTarget = EffectPrefixCaptureTarget.Evaluate(probe["native_result"]!.AsObject(), owner,
                    cache["terminal_effect_id"]!.GetValue<int>(), EffectPrefixCaptureTarget.LayerName(pristine, owner));
                if (captureTarget["status"]?.GetValue<string>() != EffectPrefixCaptureTarget.LayerTargetStatus)
                {
                    result["groups"]!.AsArray().Add(new JsonObject { ["id"] = "effect-prefix-" + owner, ["status"] = "rejected_capture_target",
                        ["owner_layer_id"] = owner, ["frames"] = frames, ["capture_target"] = captureTarget, ["period"] = loop });
                    result["status"] = "candidate_rejected_capture_target";
                    result["reason"] = captureTarget["reason"]!.DeepClone();
                    result["reason_localized"] = captureTarget["reason_localized"]!.DeepClone();
                    await Save(); return result;
                }
                uint sourceWidth = captureSource["width"]?.GetValue<uint>() ?? 0, sourceHeight = captureSource["height"]?.GetValue<uint>() ?? 0;
                if (sourceWidth == 0 || sourceHeight == 0) throw new InvalidDataException("Terminal metadata probe reported an invalid capture extent.");
                byte[] probePixels = await File.ReadAllBytesAsync(probe["rgba_path"]!.GetValue<string>(), cancellationToken);
                if (probePixels.Length != 64 * 64 * 4) throw new InvalidDataException("The terminal metadata probe has an unexpected pixel count.");
                bool packedAlpha = false;
                for (int pixel = 3; pixel < probePixels.Length; pixel += 4) packedAlpha |= probePixels[pixel] != byte.MaxValue;
                if (!packedAlpha && (sourceWidth != 64 || sourceHeight != 64))
                {
                    // A reduced probe can miss a thin transparent edge. Decide RGB versus
                    // packed alpha on one native-size frame before starting the full capture.
                    JsonObject opacityProbe;
                    using (timing.Measure(StageTiming.MasterRender))
                    opacityProbe = await runner.RenderRawAsync(new(captureProject, settings.Assets,
                        Path.Combine(cacheOutput, "opacity"), sourceWidth, sourceHeight,
                        settings.FpsNumerator, settings.FpsDenominator, 1, Seed: 17, CaptureTarget: target,
                        UserProperties: snapshot, DeviceUuid: request.DeviceUuid ?? settings.DeviceUuid), cancellationToken);
                    string opacityPath = opacityProbe["rgba_path"]!.GetValue<string>();
                    byte[] opacityPixels = await File.ReadAllBytesAsync(opacityPath, cancellationToken);
                    for (int pixel = 3; pixel < opacityPixels.Length; pixel += 4)
                        if (opacityPixels[pixel] != byte.MaxValue) { packedAlpha = true; break; }
                    if (!request.KeepIntermediates)
                        TemporaryCaptureFiles.Delete(result, Path.GetDirectoryName(opacityPath)!, Path.GetFileName(opacityPath));
                }
                bool puppetAtlas = source.ReadJson(cache["source_image"]!.GetValue<string>()).ContainsKey("puppet");
                (uint encodeWidth, uint encodeHeight) = puppetAtlas
                    ? FitAtlas(sourceWidth, sourceHeight, settings, plan)
                    : Fit(sourceWidth, sourceHeight, settings.Width, settings.Height);
                (encodeWidth, encodeHeight) = HardwareDecodeDimensions.FitCeiling(encodeWidth, encodeHeight, packedAlpha);
                // 编码尺寸与透明打包在这里定稿：越硬解上限已在上一行等比缩进上限，这里只剩过小时居中补边。
                HardwareDecodeDimensions.Plan decodePlan = HardwareDecodeDimensions.Evaluate(encodeWidth, encodeHeight, packedAlpha,
                    settings.FpsNumerator, settings.FpsDenominator);
                uint storedWidth = decodePlan.StoredWidth, storedHeight = decodePlan.StoredHeight;
                EncodedContentRegion? paddedContent = decodePlan.Padded
                    ? new((int)decodePlan.PaddedWidth, (int)decodePlan.PaddedHeight, (int)decodePlan.OffsetX, (int)decodePlan.OffsetY,
                        (int)encodeWidth, (int)encodeHeight)
                    : null;
                JsonObject rendered;
                string renderOutput = Path.Combine(cacheOutput, "encoded");
                string? encoderFallback = playbackFallback;
                var softwareRender = new RenderRequest(captureProject, settings.Assets, renderOutput, sourceWidth, sourceHeight,
                    settings.FpsNumerator, settings.FpsDenominator, checked(frames + 1), Seed: 17, CaptureTarget: target, UserProperties: snapshot,
                    EncodedFrames: frames, RetainFrames: LoopClosureCheck.ReferenceFrameIndices(frames),
                    DeviceUuid: request.DeviceUuid ?? settings.DeviceUuid, TraceScene: true, RequireOpaquePixels: !packedAlpha,
                    PixelPacking: packedAlpha ? "rgba_side_by_side" : "rgb", EncodeWidth: encodeWidth, EncodeHeight: encodeHeight,
                    OfflineVideoRateOverrides: HybridBakeService.SelectVideoRateOverrides(loop, new HashSet<int> { owner }),
                    EncodePadding: paddedContent is null ? null
                        : new(decodePlan.PaddedWidth, decodePlan.PaddedHeight, decodePlan.OffsetX, decodePlan.OffsetY));
                RenderRequest renderRequest = softwareRender;
                if (playbackKind == PlaybackEncoderSelection.Vulkan && paddedContent is null)
                {
                    bool resize = encodeWidth != sourceWidth || encodeHeight != sourceHeight;
                    string codec = PlaybackEncodeProfile.HardwareEncoder(PlaybackEncodeProfile.SelectPlaybackEncoder(
                        storedWidth, storedHeight, settings.FpsNumerator, settings.FpsDenominator), PlaybackEncoderSelection.Vulkan);
                    renderRequest = softwareRender with { GpuEncoding = new(codec, Qp: 12, RetainQualitySamples: true),
                        RequireOpaquePixels = false, CollectAlphaBounds = !packedAlpha,
                        EncodeWidth = resize ? encodeWidth : null, EncodeHeight = resize ? encodeHeight : null };
                }
                else if (PlaybackEncoderSelection.Normalize(request.PlaybackEncoder) != PlaybackEncoderSelection.Software)
                    encoderFallback ??= paddedContent is not null
                        ? "GPU prefix encoding does not support decoder padding yet; using the existing software path."
                        : "This effect-prefix path supports software or same-device Vulkan encoding.";
                try
                {
                    // 与分组源周期路线一样多渲 1 帧：编码只取前 P 帧，第 0、P−1、P 帧原帧留作闭合检查与接缝参照。
                    // 编码尺寸（Fit/FitAtlas）只由捕获范围与投影决定，与帧数无关。
                    using (timing.Measure(StageTiming.MasterRender))
                    {
                        try { rendered = await runner.RenderAsync(renderRequest, progress, cancellationToken); }
                        catch (GpuEncodeUnavailableException error) when (renderRequest.GpuEncoding is not null && !cancellationToken.IsCancellationRequested)
                        {
                            encoderFallback = error.Message;
                            renderOutput = Path.Combine(cacheOutput, "encoded-software");
                            rendered = await runner.RenderAsync(softwareRender with { OutputDirectory = renderOutput }, progress, cancellationToken);
                        }
                    }
                }
                catch (Exception error) when (!packedAlpha && !cancellationToken.IsCancellationRequested &&
                                              NativeRenderRunner.OpaquePixelEvidence(error) is { } nonOpaque)
                {
                    // 首帧不透明不代表完整动画始终不透明；后续帧变化仍按拒绝收尾，不丢弃透明度。
                    (JsonObject group, Message reason) = OpaqueCaptureRejection(pristine, owner, cache["terminal_effect_id"]!.GetValue<int>(),
                        frames, sourceWidth, sourceHeight, loop, nonOpaque);
                    result["groups"]!.AsArray().Add(group);
                    result["status"] = "candidate_rejected_opaque_capture";
                    reason.Write(result, "reason");
                    await Save(); return result;
                }
                timing.AddMasterBreakdown(rendered);
                bool gpuDirect = rendered["native_frame_transport"]?.GetValue<string>() == "gpu_nv12";
                var encodeInfo = new JsonObject { ["encoder_used"] = gpuDirect ? "vulkan" : "software",
                    ["encoder_fallback_reason"] = encoderFallback, ["encode_seconds"] = null,
                    ["readback_frames"] = rendered["readback_frames"]?.DeepClone(),
                    ["readback_bytes"] = rendered["readback_bytes"]?.DeepClone(),
                    ["gpu_resize"] = rendered["gpu_resize"]?.DeepClone() };
                JsonObject fullRuntime = rendered["native_result"]?.AsObject()
                    ?? throw new InvalidDataException("The complete prefix capture omitted its runtime evidence.");
                JsonArray fullProposals = EffectPrefixPlanner.Propose(pristine, source, settings.Assets, fullRuntime, snapshot,
                    settings, projection);
                if (!fullProposals.OfType<JsonObject>().Any(value => value["owner_layer_id"]?.GetValue<int>() == owner &&
                    value["prefix_effect_count"]?.GetValue<int>() >= prefix))
                    throw new InvalidDataException($"The complete capture found a late dependency in effect-prefix owner {owner}; no cache was applied.");
                string video = rendered["video_path"]?.GetValue<string>() ?? Path.Combine(renderOutput, "preview.mp4");
                JsonObject seam;
                JsonObject? gpuQuality = null;
                using (timing.Measure(StageTiming.SeamCheck))
                {
                    try
                    {
                        byte[] firstFrame = await LoopClosureCheck.ReadRetainedFrameAsync(rendered, 0, cancellationToken);
                        byte[] wrapFrame = await LoopClosureCheck.ReadRetainedFrameAsync(rendered, frames, cancellationToken);
                        if (gpuDirect && !packedAlpha)
                        {
                            JsonObject bounds = rendered["alpha_bounds"]!.AsObject();
                            if (bounds["observed_frames"]?.GetValue<ulong>() != frames)
                                throw new InvalidDataException("GPU opacity evidence does not cover every encoded source frame.");
                            int minimum = bounds["minimum_alpha"]!.GetValue<int>();
                            // The ordinary unencoded P frame is retained but not GPU-reduced.
                            for (int pixel = 3; pixel < wrapFrame.Length; pixel += 4) minimum = Math.Min(minimum, wrapFrame[pixel]);
                            rendered["opaque_pixels"] = new JsonObject { ["requested"] = true, ["verified"] = minimum == 255,
                                ["checked_frames"] = frames + 1, ["checked_pixels"] = checked((ulong)sourceWidth * sourceHeight * (frames + 1)),
                                ["minimum_alpha"] = minimum,
                                ["basis"] = "Native-size GPU alpha reduction on all encoded source frames, plus the retained original P frame." };
                        }
                        JsonObject closure = LoopClosureCheck.Evaluate(firstFrame, wrapFrame,
                            (int)sourceWidth, (int)sourceHeight, withAlpha: packedAlpha, frames, judged: true,
                            SwayRecurrenceSolver.SpeedLimitScale(settings.Width, settings.Height));
                        if (!request.KeepIntermediates && LoopClosureCheck.Allows(closure))
                            seam = await EncodedLoopValidator.ValidateAsync(video, tools, frames, settings.FpsNumerator,
                                settings.FpsDenominator, packedAlpha, closure, (int)encodeWidth * (packedAlpha ? 2 : 1),
                                (int)encodeHeight, paddedContent, cancellationToken);
                        else
                        {
                            LoopReference reference = await EncodedLoopValidator.FromScaledRenderAsync(tools, rendered, (int)encodeWidth,
                                (int)encodeHeight, packedAlpha, frames, closure, cancellationToken);
                            seam = await EncodedLoopValidator.ValidateAsync(video, tools, frames, settings.FpsNumerator, settings.FpsDenominator,
                                packedAlpha, reference, paddedContent, cancellationToken);
                        }
                        if (gpuDirect && seam["status"]?.GetValue<string>() == "observed_seam_pass")
                        {
                            string qualityOutput = Path.Combine(cacheOutput, "quality");
                            Directory.CreateDirectory(qualityOutput);
                            gpuQuality = await runner.GpuPlaybackQualityAsync(rendered, video,
                                new((int)sourceWidth, (int)sourceHeight, 0, 0, (int)sourceWidth, (int)sourceHeight),
                                packedAlpha, qualityOutput, cancellationToken);
                        }
                    }
                    finally
                    {
                        // 原帧只为这一次校验存在；缓存目录会随候选保留，不能把几帧原始 RGBA 留在里面。
                        if (!request.KeepIntermediates)
                            foreach (string path in new[] { rendered["retained_frames"]?["path"]?.GetValue<string>(),
                                rendered["alpha_bounds"]?["first_frame_rgba_path"]?.GetValue<string>() }.OfType<string>())
                                TemporaryCaptureFiles.Delete(result, Path.GetDirectoryName(path)!, Path.GetFileName(path));
                    }
                }
                JsonObject? seamPreview = null;
                if (SeamPreview.ShouldExport(false, false, seam, request.KeepIntermediates))
                {
                    progress?.Report(new("exporting_seam_preview", 0,
                        MessageCatalog.Get("progress.exporting_seam_preview", MessageCatalog.DefaultLanguage(),
                            SeamPreview.WindowFrames(frames, settings.FpsNumerator, settings.FpsDenominator))));
                    using (timing.Measure(StageTiming.SeamCheck))
                    seamPreview = await SeamPreview.ExportOrWarnAsync(result, "effect-prefix-" + owner,
                        SeamPreview.Outcome(seam["status"]?.GetValue<string>()),
                        token => runner.ExportSeamPreviewAsync(video, Path.Combine(cacheOutput, SeamPreview.FileName), frames,
                            settings.FpsNumerator, settings.FpsDenominator, "encoded_video", token), cancellationToken);
                }
                JsonObject? opaque = rendered["opaque_pixels"] as JsonObject;
                // 不透明扫描覆盖渲染器交出的每一帧，包括只作参照、不进编码的第 P 帧。
                bool opaquePass = packedAlpha || opaque?["requested"]?.GetValue<bool>() == true && opaque["verified"]?.GetValue<bool>() == true &&
                    opaque["checked_frames"]?.GetValue<ulong>() == frames + 1 && opaque["checked_pixels"]?.GetValue<ulong>() ==
                    checked((ulong)sourceWidth * sourceHeight * (frames + 1)) && opaque["minimum_alpha"]?.GetValue<int>() == 255;
                bool seamPassed = seam["status"]?.GetValue<string>() == "observed_seam_pass";
                JsonObject hardware = new() { ["status"] = "not_performed" };
                if (seamPassed && gpuQuality?["passed"]?.GetValue<bool>() != false && opaquePass)
                    using (timing.Measure(StageTiming.HardwareDecodeCheck))
                        hardware = await runner.ProbeHardwareDecodeAsync(video, Path.Combine(cacheOutput, "hardware-decode"), Math.Min(frames, 5), cancellationToken);
                string? rejection = !seamPassed ? "seam" : gpuQuality?["passed"]?.GetValue<bool>() == false ? "quality" :
                    !opaquePass ? "opaque_capture" : hardware["all_adapters_passed"]?.GetValue<bool>() != true ? "hardware_decode" : null;
                if (rejection is not null)
                {
                    var rejectedGroup = new JsonObject { ["id"] = "effect-prefix-" + owner,
                        ["status"] = "rejected_" + rejection,
                        ["owner_layer_id"] = owner, ["frames"] = frames, ["source_extent"] = new JsonArray(sourceWidth, sourceHeight),
                        ["encoded_extent"] = new JsonArray(storedWidth, storedHeight), ["packed_alpha"] = packedAlpha, ["video_path"] = video, ["period"] = loop,
                        ["capture_target"] = captureTarget, ["hardware_decode_preflight"] = decodePlan.ToJson(),
                        ["encoded_loop_validation"] = seam, ["hardware_decode"] = hardware, ["opaque_pixels"] = opaque?.DeepClone(),
                        ["playback_encode"] = encodeInfo, ["playback_quality_gate"] = gpuQuality, ["video_bytes"] = new FileInfo(video).Length };
                    SeamPreview.Attach(rejectedGroup, seamPreview);
                    result["groups"]!.AsArray().Add(rejectedGroup);
                    result["status"] = "candidate_rejected_" + rejection;
                    bool seamRejected = seam["status"]?.GetValue<string>() != "observed_seam_pass";
                    if (!seamRejected)
                        new Message(rejection == "quality" ? "bake.effect_prefix_quality_rejected"
                            : rejection == "hardware_decode" ? "bake.effect_prefix_hardware_decode_rejected"
                            : "bake.effect_prefix_opaque_unproven").Write(result, "reason");
                    else result["reason"] = MessageCatalog.Get("bake.effect_prefix_seam_rejected", MessageCatalog.English, EncodedLoopValidator.RejectionDetail(seam, MessageCatalog.English));
                    if (seamRejected)
                        result["reason_localized"] = new JsonObject { ["key"] = "bake.effect_prefix_seam_rejected",
                            ["zh"] = MessageCatalog.Get("bake.effect_prefix_seam_rejected", MessageCatalog.Chinese, EncodedLoopValidator.RejectionDetail(seam, MessageCatalog.Chinese)),
                            ["en"] = result["reason"]!.DeepClone(), ["params"] = new JsonArray() };
                    await Save(); return result;
                }
                using (timing.Measure(StageTiming.ProjectAssembly))
                    await EffectPrefixCache.ApplyAsync(source, pristine, candidateScene, candidateProject, owner, prefix, video,
                        storedWidth, storedHeight, false, cancellationToken, sourceWidth, sourceHeight, packedAlpha, paddedContent);
                var encodedGroup = new JsonObject { ["id"] = "effect-prefix-" + owner, ["status"] = "encoded",
                    ["owner_layer_id"] = owner, ["frames"] = frames, ["source_extent"] = new JsonArray(sourceWidth, sourceHeight),
                    ["encoded_extent"] = new JsonArray(storedWidth, storedHeight), ["logical_encoded_extent"] = new JsonArray(encodeWidth, encodeHeight),
                    ["sampling_basis"] = puppetAtlas ? "source_atlas_at_projected_canvas_density" : "source_image_fits_output",
                    ["packed_alpha"] = packedAlpha, ["video_path"] = video, ["period"] = loop, ["capture_target"] = captureTarget,
                    ["hardware_decode_preflight"] = decodePlan.ToJson(),
                    ["encoded_loop_validation"] = seam, ["hardware_decode"] = hardware, ["opaque_pixels"] = opaque?.DeepClone(),
                    ["playback_encode"] = encodeInfo, ["playback_quality_gate"] = gpuQuality, ["video_bytes"] = new FileInfo(video).Length };
                SeamPreview.Attach(encodedGroup, seamPreview);
                result["groups"]!.AsArray().Add(encodedGroup);
            }
            ProjectWriter.ApplyPropertySnapshot(candidateMetadata, snapshot); candidateMetadata["file"] = source.SceneResource;
            ProjectWriter.ApplyPropertySnapshot(referenceMetadata, snapshot); referenceMetadata["file"] = source.SceneResource;
            JsonObject propertyReport = ApplyCachedPropertyPresentation(candidateMetadata, candidateScene, cachedPropertyKeys);
            result["effect_prefix_fixed_properties"] = propertyReport;
            using (timing.Measure(StageTiming.ProjectAssembly))
            {
                await File.WriteAllTextAsync(ProjectSource.ContainedPath(candidateProject, source.SceneResource), candidateScene.ToJsonString(), cancellationToken);
                await File.WriteAllTextAsync(ProjectSource.ContainedPath(referenceProject, source.SceneResource), referenceScene.ToJsonString(), cancellationToken);
                await File.WriteAllTextAsync(Path.Combine(candidateProject, "project.json"), candidateMetadata.ToJsonString(), cancellationToken);
                await File.WriteAllTextAsync(Path.Combine(referenceProject, "project.json"), referenceMetadata.ToJsonString(), cancellationToken);
            }
            PairedComparison comparison;
            using (timing.Measure(StageTiming.CompositionValidation))
            comparison = await new CandidateValidation(tools).CompareAsync(new(1, referenceProject, candidateProject, settings.Assets,
                Path.Combine(output, "composition-validation"), settings.Width, settings.Height, settings.FpsNumerator, settings.FpsDenominator,
                CompositionGate.RequiredFrames, 0, 17, request.DeviceUuid ?? settings.DeviceUuid,
                snapshot), progress, cancellationToken);
            JsonObject composition = CompositionGate.Evaluate(comparison);
            result["composition_validation"] = composition;
            if (composition["status"]?.GetValue<string>() == CandidateScriptErrorGate.RejectedCompositionStatus)
            {
                result["status"] = CandidateScriptErrorGate.RejectedBakeStatus;
                result["reason"] = composition["reason"]?.DeepClone();
                result["reason_zh"] = composition["reason_zh"]?.DeepClone();
                result["reason_en"] = composition["reason_en"]?.DeepClone();
                await Save(); return result;
            }
            if (composition["status"]?.GetValue<string>() != "composition_pass")
            { result["status"] = "candidate_rejected_composition"; new Message("bake.effect_prefix_composition_failed").Write(result, "reason"); await Save(); return result; }
            if (hash != await source.SourceHashAsync(cancellationToken)) throw new IOException("Source changed during effect-prefix bake.");
            result["status"] = "candidate_generated"; result["loop_validation"] = "encoded_seams_passed"; result["project_path"] = candidateProject;
            await Save(); return result;
        }
        catch (Exception error)
        {
            result["status"] = cancellationToken.IsCancellationRequested ? "cancelled" : "failed";
            result["error_type"] = error.GetType().Name; result["error"] = error.Message; await Save(); throw;
        }
    }

    /// <summary>
    /// 不透明规划被全分辨率捕获推翻时的拒绝记录：组条目带上预探测结论与逐帧扫描证据，理由点名层、坐标与 alpha。
    /// 纯函数，不读写文件。
    /// </summary>
    internal static (JsonObject Group, Message Reason) OpaqueCaptureRejection(JsonObject scene, int owner, int terminalEffectId,
        ulong frames, uint sourceWidth, uint sourceHeight, JsonObject loop, JsonObject evidence)
    {
        string? name = scene["objects"]?.AsArray().OfType<JsonObject>()
            .FirstOrDefault(node => SceneGraph.Id(node) == owner)?["name"] is JsonValue value &&
            value.TryGetValue<string>(out string? text) ? text : null;
        // 证据里的数是内存里直接建的 byte/int/ulong 值，TryGetValue<int> 跨类型会失败；按 JSON 文本解析最稳。
        int Number(string key) => evidence[key] is JsonValue number && int.TryParse(number.ToJsonString(),
            System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed) ? parsed : -1;
        var reason = new Message("bake.effect_prefix_nonopaque_capture", [owner, MessageCatalog.EscapeName(name),
            Number("first_nonopaque_frame"), Number("x"), Number("y"), Number("alpha"),
            Number("nonopaque_pixels_in_frame"), Number("minimum_alpha_in_frame")]);
        var group = new JsonObject { ["id"] = "effect-prefix-" + owner, ["status"] = "rejected_opaque_capture",
            ["owner_layer_id"] = owner, ["terminal_effect_id"] = terminalEffectId, ["frames"] = frames,
            ["source_extent"] = new JsonArray(sourceWidth, sourceHeight), ["packed_alpha"] = false,
            ["probe_opacity"] = new JsonObject { ["width"] = sourceWidth, ["height"] = sourceHeight, ["minimum_alpha"] = 255,
                ["basis"] = "The native-size first-frame opacity probe had no pixel below alpha 255; later frames are still checked during capture." },
            ["period"] = loop.DeepClone(), ["opaque_pixels"] = evidence.DeepClone() };
        return (group, reason);
    }

    internal static (uint Width, uint Height) Fit(uint sourceWidth, uint sourceHeight, uint targetWidth, uint targetHeight)
    {
        double scale = Math.Min(1, Math.Min(targetWidth / (double)sourceWidth, targetHeight / (double)sourceHeight));
        uint width = Math.Max(2, (uint)Math.Floor(sourceWidth * scale / 2) * 2);
        uint height = Math.Max(2, (uint)Math.Floor(sourceHeight * scale / 2) * 2);
        return (width, height);
    }

    private static (uint Width, uint Height) FitAtlas(uint sourceWidth, uint sourceHeight, HybridAnalyzeRequest settings, JsonObject plan) =>
        FitAtlas(sourceWidth, sourceHeight, settings.Width, settings.Height,
            plan["projection"]?["visible_width"]?.GetValue<double>() ?? 0, plan["projection"]?["visible_height"]?.GetValue<double>() ?? 0);

    internal static (uint Width, uint Height) FitAtlas(uint sourceWidth, uint sourceHeight, uint targetWidth, uint targetHeight,
        double viewWidth, double viewHeight)
    {
        if (!double.IsFinite(viewWidth) || !double.IsFinite(viewHeight) || viewWidth <= 0 || viewHeight <= 0)
            throw new InvalidDataException("A puppet atlas needs the source camera extent to preserve its projected pixel density.");
        double scale = Math.Min(1, Math.Max(targetWidth / viewWidth, targetHeight / viewHeight));
        return (Math.Max(2, (uint)Math.Floor(sourceWidth * scale / 2) * 2),
            Math.Max(2, (uint)Math.Floor(sourceHeight * scale / 2) * 2));
    }

    private static JsonObject ApplyCachedPropertyPresentation(JsonObject project, JsonObject candidateScene, IReadOnlySet<string> keys)
    {
        var removed = new JsonArray(); var retained = new JsonArray();
        JsonArray KeyJson() => new(keys.Order().Select(key => (JsonNode?)JsonValue.Create(key)).ToArray());
        if (project["general"]?["properties"] is not JsonObject properties) return new() { ["keys"] = KeyJson(), ["removed"] = removed, ["retained"] = retained };
        foreach (string key in keys.Order())
        {
            if (properties[key] is not JsonObject property) continue;
            if (!HasPropertyConsumer(candidateScene, project, key)) { properties.Remove(key); removed.Add(key); continue; }
            string label = property["text"]?.GetValue<string>() ?? key;
            property["text"] = label + " · Cached part fixed / 缓存部分已固定";
            retained.Add(key);
        }
        if (retained.Count > 0)
        {
            string noticeKey = "wpebakereffectprefixnotice";
            while (properties.ContainsKey(noticeKey)) noticeKey += "0";
            properties[noticeKey] = new JsonObject { ["type"] = "text", ["value"] = "", ["order"] = -1, ["index"] = -1,
                ["text"] = "Cached part fixed; change settings and regenerate. / 缓存部分已固定；改设置后请重新生成。" };
        }
        return new() { ["keys"] = KeyJson(), ["removed"] = removed, ["retained"] = retained };
    }

    private static bool HasPropertyConsumer(JsonNode root, JsonObject project, string key)
    {
        bool ScriptUses(JsonObject node) => node["script"] is JsonValue script && script.TryGetValue<string>(out string? text) &&
            text.Contains(key, StringComparison.Ordinal);
        return SceneAnalyzer.Walk(root).OfType<JsonObject>().Any(node =>
            (node["user"] is JsonObject binding ? binding["name"] : node["user"]) is JsonValue user &&
                user.TryGetValue<string>(out string? value) && value == key || ScriptUses(node)) ||
            SceneAnalyzer.Walk(project).OfType<JsonObject>().Any(ScriptUses);
    }
}
