using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 合成探针：同一计划先烘 48 帧短探针工程，再从原作造参照工程，两边在同一组输入下成对渲染比较，最后过
/// <see cref="CompositionGate"/>。短烘焙仍递归走 <see cref="HybridBakeService.BakeAsync"/>（ProbeFrames = 48），
/// 与成品共用同一条组流水线（捕获副本、组主渲染调度、组成品编码）。
/// </summary>
internal sealed class ProbeBake(NativeTools tools)
{
    /// <summary>探针、参照与比较结果分别放在 <paramref name="output"/> 旁的 .composition-probe / -reference / -validation。</summary>
    internal async Task<JsonObject> ValidateAsync(HybridBakeService baker, HybridBakeRequest request, JsonObject plan,
        HybridAnalyzeRequest settings, ProjectSource source, string output, IProgress<RenderProgress>? progress,
        CancellationToken cancellationToken)
    {
        string probeOutput = output + ".composition-probe";
        HybridBakeService.EnsureNewDerivedOutput(source, probeOutput, "Composition probe output");
        string? selectedDevice = request.DeviceUuid ?? settings.DeviceUuid;
        progress?.Report(new("checking_composition", 0, "Generating a short candidate to check complete scene composition."));
        JsonObject probe = await baker.BakeAsync(new(2, plan, probeOutput, CompositionGate.RequiredFrames,
            selectedDevice, EffectRenderScale: request.EffectRenderScale,
            MatchEffectResolution: request.MatchEffectResolution), progress, cancellationToken);
        // 探针跑完后与原来单独的比较段一样：按计划重新打开源、重读设置并复核哈希，短烘焙期间源被改过就不比了。
        var planSettings = PlanSettings.Of(plan);
        using var planSource = new ProjectSource(plan["source"]?.GetValue<string>()
            ?? throw new InvalidDataException("Hybrid plan source is missing."));
        if (await planSource.SourceHashAsync(cancellationToken) != plan["source_sha256"]?.GetValue<string>())
            throw new InvalidDataException("Source changed; analyze it again.");
        string comparisonReference = output + ".composition-reference";
        string comparisonOutput = output + ".composition-validation";
        HybridBakeService.EnsureNewDerivedOutput(planSource, comparisonReference, "Composition reference output");
        HybridBakeService.EnsureNewDerivedOutput(planSource, comparisonOutput, "Composition validation output");
        if (probe["status"]?.GetValue<string>() != "probe_generated")
            throw new InvalidDataException("The short composition probe did not produce a project for paired comparison.");
        string project = Path.GetFullPath(probe["project_path"]?.GetValue<string>()
            ?? throw new InvalidDataException("The short composition probe omitted its project path."));
        string probeDirectory = Path.GetDirectoryName(project)
            ?? throw new InvalidDataException("The short composition probe project path has no parent directory.");
        string captureSource = Path.Combine(probeDirectory, "capture-source");
        JsonObject comparisonProperties = plan["snapshot_properties"]!.DeepClone().AsObject();
        if (plan["daytime_split"]?["controls_video_playback"]?.GetValue<bool>() == true && planSettings.DaytimeState is not null)
        {
            JsonObject referenceScene = planSource.ReadJson(planSource.SceneResource);
            PlanTransforms.ApplyAudioEffectChoice(referenceScene, plan);
            var runtime = JsonNode.Parse(await File.ReadAllTextAsync(plan["runtime_evidence"]!.GetValue<string>(), cancellationToken))!.AsObject();
            var daytime = DaytimeSplit.PrepareDynamicExport(referenceScene["objects"]!.AsArray().OfType<JsonObject>()
                .ToDictionary(SceneGraph.Id), plan, runtime["runtime_dependencies"]!.AsArray())!;
            comparisonProperties = daytime.ComparisonProperties(comparisonProperties);
        }
        // 单次入场动画（bake.json 的 intro_live）：入场段显示原作图层时从第 0 帧比到切换后 48 帧；没做成就只比入场结束后。
        ulong introFrames = probe["intro_live"]?["intro_frames"]?.GetValue<ulong>() ?? 0;
        bool introLive = probe["intro_live"]?["status"]?.GetValue<string>() == "applied";
        await CreateReferenceAsync(planSource, comparisonReference, comparisonProperties, planSettings.ViewMode, plan, cancellationToken);
        PairedComparison comparison = await new CandidateValidation(tools).CompareAsync(new ValidationRequest(
            1, comparisonReference, project, planSettings.Assets, comparisonOutput, planSettings.Width, planSettings.Height,
            planSettings.FpsNumerator, planSettings.FpsDenominator, CompositionGate.RequiredFrames + (introLive ? introFrames : 0),
            WarmupFrames: introLive ? 0 : introFrames, Seed: 17, DeviceUuid: planSettings.DeviceUuid,
            UserProperties: comparisonProperties,
            Input: new JsonObject { ["cursor_x"] = .5, ["cursor_y"] = .5, ["cursor_in_window"] = true },
            TileSize: (uint)Math.Round(CompositionGate.RequiredTileSize *
                SwayRecurrenceSolver.SpeedLimitScale(planSettings.Width, planSettings.Height))), progress, cancellationToken);
        JsonObject validation = CompositionGate.Evaluate(comparison);
        validation["probe_output_path"] = probeDirectory;
        validation["probe_capture_source_path"] = captureSource;
        validation["comparison_reference_path"] = comparisonReference;
        validation["probe_project_path"] = project;
        validation["comparison_output_path"] = comparisonOutput;
        validation["occlusion_tradeoff"] = plan["occlusion_tradeoff"]?.DeepClone();
        validation["text_effects_choice"] = plan["text_effects_choice"]?.DeepClone();
        validation["audio_effects_choice"] = plan["audio_effects_choice"]?.DeepClone();
        if (plan["daytime_split"]?["controls_video_playback"]?.GetValue<bool>() == true)
            validation["daytime_state_under_test"] = planSettings.DaytimeState;
        return validation;
    }

    /// <summary>参照工程：原作解包，套音频/叠加层/文字效果取舍与属性快照；固定视角时关掉视差，镜头抖动保留。</summary>
    internal static async Task CreateReferenceAsync(ProjectSource source, string destination,
        JsonObject snapshot, string viewMode, JsonObject plan, CancellationToken cancellationToken)
    {
        JsonObject scene = source.ReadJson(source.SceneResource);
        JsonObject metadata = source.Contains("project.json") ? source.ReadJson("project.json") : new JsonObject();
        PlanTransforms.ApplyAudioEffectChoice(scene, plan);
        if (viewMode == "fixed_view") scene["general"]!["cameraparallax"] = false;
        PlanTransforms.ApplyOverlayPlacement(scene, plan);
        PlanTransforms.ApplyTextEffectChoice(scene, plan);
        ProjectWriter.ApplyPropertySnapshot(metadata, snapshot);
        metadata["file"] = source.SceneResource;
        await source.ExtractAsync(destination, cancellationToken);
        await File.WriteAllTextAsync(ProjectSource.ContainedPath(destination, source.SceneResource),
            scene.ToJsonString(), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(destination, "project.json"), metadata.ToJsonString(), cancellationToken);
    }
}
