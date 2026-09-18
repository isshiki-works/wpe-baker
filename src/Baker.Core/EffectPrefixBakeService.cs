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
        JsonObject node = scene["objects"]?.AsArray().OfType<JsonObject>().SingleOrDefault(x => HybridScenePlanner.Id(x) == owner)
            ?? throw new InvalidDataException("Effect-prefix cache owner is absent from the source scene.");
        if (prefix <= 0 || node["effects"] is not JsonArray effects || prefix > effects.Count ||
            effects[prefix - 1]?["id"]?.GetValue<int>() != cache["terminal_effect_id"]?.GetValue<int>() ||
            node["image"]?.GetValue<string>() != cache["source_image"]?.GetValue<string>() ||
            cache["loop"] is not JsonObject || cache["fixed_user_properties"] is not JsonObject)
            throw new InvalidDataException("Effect-prefix cache description no longer matches its source owner, terminal effect, or fixed properties.");
        if (!source.Contains(node["image"]!.GetValue<string>())) throw new InvalidDataException("Effect-prefix cache owner image must be project-owned.");
        EffectPrefixCache.ValidateSource(source, scene, scene, owner, prefix);
    }

    internal async Task<JsonObject> BakeAsync(HybridBakeRequest request, IProgress<RenderProgress>? progress,
        StageTiming timing, CancellationToken cancellationToken = default)
    {
        if (request.SchemaVersion != 2 || request.ProbeFrames != 0) throw new InvalidDataException("Effect-prefix baking requires a production version 2 request.");
        JsonObject plan = request.Plan.DeepClone().AsObject();
        if (plan["effect_prefix_caches"] is not JsonArray { Count: > 0 } caches)
            throw new InvalidDataException("The plan has no effect_prefix_caches.");
        HybridAnalyzeRequest settings = plan["settings"]?.Deserialize<HybridAnalyzeRequest>(JsonOptions)
            ?? throw new InvalidDataException("Effect-prefix settings are missing.");
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
        // 前缀循环在 bake 侧按同一个入口重算，投影记录取 plan 里那一份（与 HybridScenePlanner.RefreshLoop 同样的取法）：
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
        var result = new JsonObject { ["schema_version"] = 2, ["artifact_kind"] = "hybrid_video_candidate",
            ["status"] = "running", ["source_sha256"] = hash, ["source_digest_scope"] = ProjectSource.DigestScope,
            ["plan"] = plan.DeepClone(), ["groups"] = new JsonArray(), ["source_start_frame"] = 0,
            ["seam_policy"] = "source_period_no_repair", ["official_playback"] = "not_verified", ["measured_gain"] = "not_verified" };
        string reportPath = Path.Combine(output, "bake.json");
        async Task Save()
        {
            timing.Stamp(result);
            await File.WriteAllTextAsync(reportPath, result.ToJsonString(JsonOptions), CancellationToken.None);
        }
        await Save();
        try
        {
            var runner = new NativeRenderRunner(tools);
            foreach (JsonObject cache in caches.OfType<JsonObject>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                int owner = cache["owner_layer_id"]!.GetValue<int>(), prefix = cache["prefix_effect_count"]!.GetValue<int>();
                var captureScene = pristine.DeepClone().AsObject();
                HybridScenePlanner.FreezeTemporalProperties(captureScene, snapshot);
                JsonObject loop = EffectPrefixPlanner.AnalyzePrefix(pristine, source, settings.Assets, runtime, snapshot,
                    owner, prefix, settings, projection);
                if (loop["unresolved"] is JsonArray { Count: > 0 } || loop["candidates"] is not JsonArray { Count: > 0 })
                    throw new InvalidDataException($"Effect-prefix owner {owner} has no complete source-derived period.");
                ulong frames = loop["candidates"]!.AsArray()[0]!["frames"]!.GetValue<ulong>();
                HybridLoopService.ApplyPatches(captureScene, loop);
                string cacheOutput = Path.Combine(output, "prefix-" + owner);
                string captureProject = Path.Combine(cacheOutput, "capture-source");
                JsonObject metadata;
                using (timing.Measure(StageTiming.SourceCapture))
                {
                    await source.ExtractAsync(captureProject, cancellationToken);
                    await File.WriteAllTextAsync(ProjectSource.ContainedPath(captureProject, source.SceneResource), captureScene.ToJsonString(), cancellationToken);
                    metadata = source.Contains("project.json") ? source.ReadJson("project.json") : new JsonObject();
                    HybridBakeService.ApplyPropertySnapshot(metadata, snapshot); metadata["file"] = source.SceneResource;
                    await File.WriteAllTextAsync(Path.Combine(captureProject, "project.json"), metadata.ToJsonString(), cancellationToken);
                }
                var target = new RenderCaptureSelection(owner, cache["terminal_effect_id"]!.GetValue<int>(), EffectTerminal: true, ExactExtent: true);
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
                bool puppetAtlas = source.ReadJson(cache["source_image"]!.GetValue<string>()).ContainsKey("puppet");
                (uint encodeWidth, uint encodeHeight) = puppetAtlas
                    ? FitAtlas(sourceWidth, sourceHeight, settings, plan)
                    : Fit(sourceWidth, sourceHeight, settings.Width, settings.Height);
                // 编码尺寸与透明打包在这里定稿：先按硬件解码限制表预检，过小就居中补边，补边后仍越上限就在编码前拒绝。
                HardwareDecodeDimensions.Plan decodePlan = HardwareDecodeDimensions.Evaluate(encodeWidth, encodeHeight, packedAlpha,
                    settings.FpsNumerator, settings.FpsDenominator);
                uint storedWidth = decodePlan.StoredWidth, storedHeight = decodePlan.StoredHeight;
                if (decodePlan.Rejected)
                {
                    string layer = $"L{owner.ToString(System.Globalization.CultureInfo.InvariantCulture)} \"{Messages.EscapeName(pristine["objects"]?.AsArray().OfType<JsonObject>()
                        .FirstOrDefault(value => HybridScenePlanner.Id(value) == owner)?["name"] is JsonValue name && name.TryGetValue(out string? text) ? text : null)}\"";
                    string extent = HardwareDecodeDimensions.Extent(storedWidth, storedHeight);
                    string reason = Messages.EmitBilingual("bake.hardware_decode_dimensions_rejected",
                        [layer, extent, decodePlan.PackingText(Messages.Chinese), decodePlan.ViolationText(Messages.Chinese), decodePlan.Limits.BasisZh],
                        [layer, extent, decodePlan.PackingText(Messages.English), decodePlan.ViolationText(Messages.English), decodePlan.Limits.BasisEn]);
                    result["groups"]!.AsArray().Add(new JsonObject { ["id"] = "effect-prefix-" + owner,
                        ["status"] = "rejected_hardware_decode_dimensions", ["owner_layer_id"] = owner, ["frames"] = frames,
                        ["source_extent"] = new JsonArray(sourceWidth, sourceHeight), ["encoded_extent"] = new JsonArray(storedWidth, storedHeight),
                        ["logical_encoded_extent"] = new JsonArray(encodeWidth, encodeHeight),
                        ["sampling_basis"] = puppetAtlas ? "source_atlas_at_projected_canvas_density" : "source_image_fits_output",
                        ["packed_alpha"] = packedAlpha, ["period"] = loop, ["hardware_decode_preflight"] = decodePlan.ToJson(),
                        ["encoded_loop_validation"] = null, ["hardware_decode"] = null });
                    result["status"] = "candidate_rejected_hardware_decode";
                    result["reason"] = reason;
                    result["reason_localized"] = Messages.Localize(reason);
                    await Save(); return result;
                }
                EncodedContentRegion? paddedContent = decodePlan.Padded
                    ? new((int)decodePlan.PaddedWidth, (int)decodePlan.PaddedHeight, (int)decodePlan.OffsetX, (int)decodePlan.OffsetY,
                        (int)encodeWidth, (int)encodeHeight)
                    : null;
                JsonObject rendered;
                try
                {
                    // 与分组源周期路线一样多渲 1 帧：编码只取前 P 帧，第 0、P−1、P 帧原帧留作闭合检查与接缝参照。
                    // 编码尺寸（Fit/FitAtlas）只由捕获范围与投影决定，与帧数无关。
                    using (timing.Measure(StageTiming.MasterRender))
                    rendered = await runner.RenderAsync(new(captureProject, settings.Assets, Path.Combine(cacheOutput, "encoded"), sourceWidth, sourceHeight,
                        settings.FpsNumerator, settings.FpsDenominator, checked(frames + 1), Seed: 17, CaptureTarget: target, UserProperties: snapshot,
                        EncodedFrames: frames, RetainFrames: LoopClosureCheck.ReferenceFrameIndices(frames),
                        DeviceUuid: request.DeviceUuid ?? settings.DeviceUuid, TraceScene: true, RequireOpaquePixels: !packedAlpha,
                        PixelPacking: packedAlpha ? "rgba_side_by_side" : "rgb",
                        EncodeWidth: encodeWidth, EncodeHeight: encodeHeight,
                        OfflineVideoRateOverrides: HybridBakeService.SelectVideoRateOverrides(loop, new HashSet<int> { owner }),
                        EncodePadding: paddedContent is null ? null
                            : new(decodePlan.PaddedWidth, decodePlan.PaddedHeight, decodePlan.OffsetX, decodePlan.OffsetY)), progress, cancellationToken);
                }
                catch (Exception error) when (!packedAlpha && !cancellationToken.IsCancellationRequested &&
                                              NativeRenderRunner.OpaquePixelEvidence(error) is { } nonOpaque)
                {
                    // 低分辨率预探测判成不透明、全分辨率捕获却读到 alpha<255：按拒绝收尾并点名层与 alpha，不再当崩溃抛出。
                    (JsonObject group, string reason) = OpaqueCaptureRejection(pristine, owner, cache["terminal_effect_id"]!.GetValue<int>(),
                        frames, sourceWidth, sourceHeight, loop, nonOpaque);
                    result["groups"]!.AsArray().Add(group);
                    result["status"] = "candidate_rejected_opaque_capture";
                    result["reason"] = reason;
                    result["reason_localized"] = Messages.Localize(reason);
                    await Save(); return result;
                }
                timing.AddMasterBreakdown(rendered);
                JsonObject fullRuntime = rendered["native_result"]?.AsObject()
                    ?? throw new InvalidDataException("The complete prefix capture omitted its runtime evidence.");
                JsonArray fullProposals = EffectPrefixPlanner.Propose(pristine, source, settings.Assets, fullRuntime, snapshot,
                    settings, projection);
                if (!fullProposals.OfType<JsonObject>().Any(value => value["owner_layer_id"]?.GetValue<int>() == owner &&
                    value["prefix_effect_count"]?.GetValue<int>() >= prefix))
                    throw new InvalidDataException($"The complete capture found a late dependency in effect-prefix owner {owner}; no cache was applied.");
                string video = rendered["video_path"]?.GetValue<string>() ?? Path.Combine(cacheOutput, "encoded", "preview.mp4");
                JsonObject seam;
                using (timing.Measure(StageTiming.SeamCheck))
                {
                    try
                    {
                        byte[] firstFrame = await LoopClosureCheck.ReadRetainedFrameAsync(rendered, 0, cancellationToken);
                        byte[] wrapFrame = await LoopClosureCheck.ReadRetainedFrameAsync(rendered, frames, cancellationToken);
                        JsonObject closure = LoopClosureCheck.Evaluate(firstFrame, wrapFrame,
                            (int)sourceWidth, (int)sourceHeight, withAlpha: packedAlpha, frames, judged: true);
                        LoopReference reference = await EncodedLoopValidator.FromScaledRenderAsync(tools, rendered, (int)encodeWidth,
                            (int)encodeHeight, packedAlpha, frames, closure, cancellationToken);
                        seam = await EncodedLoopValidator.ValidateAsync(video, tools, frames, settings.FpsNumerator, settings.FpsDenominator,
                            packedAlpha, reference, paddedContent, cancellationToken);
                    }
                    finally
                    {
                        // 原帧只为这一次校验存在；缓存目录会随候选保留，不能把几帧原始 RGBA 留在里面。
                        TemporaryCaptureFiles.Delete(result, Path.Combine(cacheOutput, "encoded"), NativeRenderRunner.RetainedFramesFile);
                    }
                }
                // 接缝校验一有结局就导出预览，通过与被拒都导；失败只记 warning，不改变后面的判定。
                JsonObject seamPreview;
                progress?.Report(new("exporting_seam_preview", 0,
                    Messages.Get("progress.exporting_seam_preview", Messages.DefaultLanguage(),
                        SeamPreview.WindowFrames(frames, settings.FpsNumerator, settings.FpsDenominator))));
                using (timing.Measure(StageTiming.SeamCheck))
                seamPreview = await SeamPreview.ExportOrWarnAsync(result, "effect-prefix-" + owner,
                    SeamPreview.Outcome(seam["status"]?.GetValue<string>()),
                    token => runner.ExportSeamPreviewAsync(video, Path.Combine(cacheOutput, SeamPreview.FileName), frames,
                        settings.FpsNumerator, settings.FpsDenominator, "encoded_video", token), cancellationToken);
                JsonObject hardware;
                using (timing.Measure(StageTiming.HardwareDecodeCheck))
                hardware = await runner.ProbeHardwareDecodeAsync(video, Path.Combine(cacheOutput, "hardware-decode"), Math.Min(frames, 5), cancellationToken);
                JsonObject? opaque = rendered["opaque_pixels"] as JsonObject;
                // 不透明扫描覆盖渲染器交出的每一帧，包括只作参照、不进编码的第 P 帧。
                bool opaquePass = packedAlpha || opaque?["requested"]?.GetValue<bool>() == true && opaque["verified"]?.GetValue<bool>() == true &&
                    opaque["checked_frames"]?.GetValue<ulong>() == frames + 1 && opaque["checked_pixels"]?.GetValue<ulong>() ==
                    checked((ulong)sourceWidth * sourceHeight * (frames + 1)) && opaque["minimum_alpha"]?.GetValue<int>() == 255;
                if (seam["status"]?.GetValue<string>() != "observed_seam_pass" || hardware["all_adapters_passed"]?.GetValue<bool>() != true || !opaquePass)
                {
                    var rejectedGroup = new JsonObject { ["id"] = "effect-prefix-" + owner,
                        ["status"] = seam["status"]?.GetValue<string>() != "observed_seam_pass" ? "rejected_seam" :
                            hardware["all_adapters_passed"]?.GetValue<bool>() != true ? "rejected_hardware_decode" : "rejected_opaque_capture",
                        ["owner_layer_id"] = owner, ["frames"] = frames, ["source_extent"] = new JsonArray(sourceWidth, sourceHeight),
                        ["encoded_extent"] = new JsonArray(storedWidth, storedHeight), ["packed_alpha"] = packedAlpha, ["video_path"] = video, ["period"] = loop,
                        ["capture_target"] = captureTarget, ["hardware_decode_preflight"] = decodePlan.ToJson(),
                        ["encoded_loop_validation"] = seam, ["hardware_decode"] = hardware, ["opaque_pixels"] = opaque?.DeepClone() };
                    SeamPreview.Attach(rejectedGroup, seamPreview);
                    result["groups"]!.AsArray().Add(rejectedGroup);
                    result["status"] = seam["status"]?.GetValue<string>() != "observed_seam_pass" ? "candidate_rejected_seam" :
                        hardware["all_adapters_passed"]?.GetValue<bool>() != true ? "candidate_rejected_hardware_decode" : "candidate_rejected_opaque_capture";
                    bool seamRejected = seam["status"]?.GetValue<string>() != "observed_seam_pass";
                    result["reason"] = seamRejected
                        ? Messages.Get("bake.effect_prefix_seam_rejected", Messages.English, EncodedLoopValidator.RejectionDetail(seam, Messages.English))
                        : hardware["all_adapters_passed"]?.GetValue<bool>() != true ? "The source-period prefix encoding did not pass the actual hardware decode check."
                        : "The full terminal capture did not prove opaque pixels for every encoded source frame.";
                    if (seamRejected)
                        result["reason_localized"] = new JsonObject { ["key"] = "bake.effect_prefix_seam_rejected",
                            ["zh"] = Messages.Get("bake.effect_prefix_seam_rejected", Messages.Chinese, EncodedLoopValidator.RejectionDetail(seam, Messages.Chinese)),
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
                    ["encoded_loop_validation"] = seam, ["hardware_decode"] = hardware, ["opaque_pixels"] = opaque?.DeepClone() };
                SeamPreview.Attach(encodedGroup, seamPreview);
                result["groups"]!.AsArray().Add(encodedGroup);
            }
            HybridBakeService.ApplyPropertySnapshot(candidateMetadata, snapshot); candidateMetadata["file"] = source.SceneResource;
            HybridBakeService.ApplyPropertySnapshot(referenceMetadata, snapshot); referenceMetadata["file"] = source.SceneResource;
            JsonObject propertyReport = ApplyCachedPropertyPresentation(candidateMetadata, candidateScene, cachedPropertyKeys);
            result["effect_prefix_fixed_properties"] = propertyReport;
            using (timing.Measure(StageTiming.ProjectAssembly))
            {
                await File.WriteAllTextAsync(ProjectSource.ContainedPath(candidateProject, source.SceneResource), candidateScene.ToJsonString(), cancellationToken);
                await File.WriteAllTextAsync(ProjectSource.ContainedPath(referenceProject, source.SceneResource), referenceScene.ToJsonString(), cancellationToken);
                await File.WriteAllTextAsync(Path.Combine(candidateProject, "project.json"), candidateMetadata.ToJsonString(), cancellationToken);
                await File.WriteAllTextAsync(Path.Combine(referenceProject, "project.json"), referenceMetadata.ToJsonString(), cancellationToken);
            }
            JsonObject comparison;
            using (timing.Measure(StageTiming.CompositionValidation))
            comparison = await new CandidateValidation(tools).ValidateAsync(new(1, referenceProject, candidateProject, settings.Assets,
                Path.Combine(output, "composition-validation"), settings.Width, settings.Height, settings.FpsNumerator, settings.FpsDenominator,
                HybridCompositionValidator.RequiredFrames, 0, 17, request.DeviceUuid ?? settings.DeviceUuid,
                snapshot, RetainRawFrames: false), progress, cancellationToken);
            JsonObject composition = HybridCompositionValidator.Evaluate(comparison);
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
            { result["status"] = "candidate_rejected_composition"; result["reason"] = "The pristine-source 48-frame composition comparison failed."; await Save(); return result; }
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
    internal static (JsonObject Group, string Reason) OpaqueCaptureRejection(JsonObject scene, int owner, int terminalEffectId,
        ulong frames, uint sourceWidth, uint sourceHeight, JsonObject loop, JsonObject evidence)
    {
        string? name = scene["objects"]?.AsArray().OfType<JsonObject>()
            .FirstOrDefault(node => HybridScenePlanner.Id(node) == owner)?["name"] is JsonValue value &&
            value.TryGetValue<string>(out string? text) ? text : null;
        // 证据里的数是内存里直接建的 byte/int/ulong 值，TryGetValue<int> 跨类型会失败；按 JSON 文本解析最稳。
        int Number(string key) => evidence[key] is JsonValue number && int.TryParse(number.ToJsonString(),
            System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed) ? parsed : -1;
        string reason = Messages.Emit("bake.effect_prefix_nonopaque_capture", owner, Messages.EscapeName(name),
            Number("first_nonopaque_frame"), Number("x"), Number("y"), Number("alpha"),
            Number("nonopaque_pixels_in_frame"), Number("minimum_alpha_in_frame"));
        var group = new JsonObject { ["id"] = "effect-prefix-" + owner, ["status"] = "rejected_opaque_capture",
            ["owner_layer_id"] = owner, ["terminal_effect_id"] = terminalEffectId, ["frames"] = frames,
            ["source_extent"] = new JsonArray(sourceWidth, sourceHeight), ["packed_alpha"] = false,
            ["probe_opacity"] = new JsonObject { ["width"] = 64, ["height"] = 64, ["minimum_alpha"] = 255,
                ["basis"] = "The one-frame 64x64 terminal metadata probe had no pixel below alpha 255, so an opaque RGB encoding was planned." },
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
