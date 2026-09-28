using System.Globalization;
using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>Reduce an authored video's coded size while keeping its layer and playback controller intact.</summary>
internal static class SourceVideoOptimization
{
    internal const string Route = "source_video_optimization";

    internal static async Task<JsonObject> BakeAsync(HybridBakeRequest request, NativeTools tools,
        IProgress<RenderProgress>? progress, StageTiming timing, CancellationToken token)
    {
        if (request.SchemaVersion != 2 || request.ProbeFrames != 0)
            throw new InvalidDataException("Source-video generation requires a version 2 full bake request.");
        JsonObject plan = request.Plan.DeepClone().AsObject();
        HybridPlanFormat.Validate(plan);
        if (plan["route"]?.GetValue<string>() != Route || plan["blockers"] is not JsonArray { Count: 0 } ||
            plan["source_video_optimization"]?["resources"] is not JsonArray { Count: > 0 } resources)
            throw new InvalidDataException("The source-video plan is incomplete; analyze again.");
        using var source = new ProjectSource(plan["source"]!.GetValue<string>());
        string sourceHash = await source.SourceHashAsync(token);
        if (sourceHash != plan["source_sha256"]!.GetValue<string>())
            throw new InvalidDataException("Source changed; analyze it again.");
        var layout = new WorkLayout(request.OutputDirectory);
        HybridBakeService.EnsureNewDerivedOutput(source, layout.Output, "Source-video output");
        string? destination = ProjectPublisher.Destination(request.ProjectDirectory, source, layout.Output);
        JsonObject fresh = plan.DeepClone().AsObject();
        fresh["route"] = "whole_layer";
        fresh["status"] = "requires_resolution";
        if (await TryPlanAsync(fresh, tools, token) is not JsonObject checkedPlan ||
            !JsonNode.DeepEquals(resources, checkedPlan["source_video_optimization"]?["resources"]))
            throw new InvalidDataException("Source-video eligibility changed; analyze again.");

        Directory.CreateDirectory(layout.Output);
        string project = Path.Combine(layout.Output, "project");
        var report = BakeReportWriter.Head("hybrid_video_candidate", "running", sourceHash);
        report["plan"] = plan;
        report["project_path"] = project;
        report["groups"] = new JsonArray();
        report["source_start_frame"] = 0;
        report["loop_validation"] = "not_applicable_source_video";
        report["source_video_optimization"] = new JsonObject { ["status"] = "running", ["resources"] = new JsonArray(),
            ["scope"] = "Only the coded video bodies of listed source TEX resources change; original layers, controller and user properties remain." };
        BakeReportWriter.AddNotVerified(report);
        await BakeReportWriter.SaveAsync(layout.Report, report, timing, token);
        using (timing.Measure(StageTiming.ProjectAssembly)) await source.ExtractAsync(project, token);
        var ff = new FfmpegTool(tools);
        var completed = report["source_video_optimization"]!["resources"]!.AsArray();
        int index = 0;
        foreach (JsonObject resource in resources.OfType<JsonObject>())
        {
            token.ThrowIfCancellationRequested();
            string name = resource["resource"]!.GetValue<string>();
            string target = ProjectSource.ContainedPath(project, name);
            string sourceVideo = Path.Combine(layout.Output, $"video-{index}-source.mp4");
            string resizedVideo = Path.Combine(layout.Output, $"video-{index}-resized.mp4");
            string wrapped = Path.Combine(layout.Output, $"video-{index}.tex");
            progress?.Report(new("encode_playback", (double)index / resources.Count,
                $"Resizing source video {index + 1}/{resources.Count}."));
            try
            {
                TextureContainer.TextureHeader original = await TextureContainer.ExtractVideoAsync(target, sourceVideo, token);
                if (original.Width != resource["source_width"]!.GetValue<uint>() ||
                    original.Height != resource["source_height"]!.GetValue<uint>())
                    throw new InvalidDataException($"Source video dimensions changed: {name}");
                uint width = resource["target_width"]!.GetValue<uint>(), height = resource["target_height"]!.GetValue<uint>();
                using (timing.Measure(StageTiming.EncodePlayback))
                    _ = await ff.RunTextAsync(tools.Ffmpeg,
                        ["-hide_banner", "-nostdin", "-y", "-i", sourceVideo, "-map", "0", "-c", "copy", "-c:v:0", "libx264",
                         "-preset", "fast", "-crf", "16", "-vf", $"scale={width}:{height}:flags=lanczos", "-pix_fmt", "yuv420p",
                         "-fps_mode:v", "passthrough", "-color_primaries", "bt709", "-color_trc", "bt709",
                         "-colorspace", "bt709", "-movflags", "+faststart", resizedVideo],
                        Path.Combine(layout.Output, $"video-{index}-encode.log"), token);
                JsonObject? media = await MediaAsync(tools, resizedVideo, Path.Combine(layout.Output, $"video-{index}-probe.log"), token);
                if (media is null || media["width"]!.GetValue<int>() != width || media["height"]!.GetValue<int>() != height ||
                    media["frame_count"]!.GetValue<ulong>() != resource["frame_count"]!.GetValue<ulong>() ||
                    media["fps_num"]!.GetValue<uint>() != resource["fps_num"]!.GetValue<uint>() ||
                    media["fps_den"]!.GetValue<uint>() != resource["fps_den"]!.GetValue<uint>() ||
                    media["audio_streams"]!.GetValue<int>() != resource["audio_streams"]!.GetValue<int>())
                    throw new InvalidDataException($"Source video stream timing or format changed: {name}");
                foreach (string stream in media["audio_streams"]!.GetValue<int>() == 0 ? ["v:0"] : new[] { "v:0", "a:0" })
                {
                    string before = await PacketTimelineAsync(ff, tools, sourceVideo, stream, Path.Combine(layout.Output, $"video-{index}-{stream[0]}-source.log"), token);
                    string after = await PacketTimelineAsync(ff, tools, resizedVideo, stream, Path.Combine(layout.Output, $"video-{index}-{stream[0]}-resized.log"), token);
                    if (before != after) throw new InvalidDataException($"Source video {stream} packet times or audio payload changed: {name}");
                }
                await TextureContainer.WriteVideoAsync(wrapped, resizedVideo, width, height, token);
                File.Move(wrapped, target, true);
                completed.Add(new JsonObject { ["layer_id"] = resource["layer_id"]!.DeepClone(), ["resource"] = name,
                    ["source_width"] = original.Width, ["source_height"] = original.Height,
                    ["target_width"] = width, ["target_height"] = height,
                    ["frame_count"] = media["frame_count"]!.DeepClone(), ["fps_num"] = media["fps_num"]!.DeepClone(),
                    ["fps_den"] = media["fps_den"]!.DeepClone(), ["audio_streams"] = media["audio_streams"]!.DeepClone(),
                    ["source_bytes"] = new FileInfo(sourceVideo).Length, ["candidate_bytes"] = new FileInfo(resizedVideo).Length,
                    ["packet_timestamps_match"] = true, ["audio_packets_match"] = true });
                await BakeReportWriter.SaveAsync(layout.Report, report, timing, token);
            }
            finally
            {
                if (!request.KeepIntermediates)
                {
                    File.Delete(sourceVideo);
                    File.Delete(resizedVideo);
                    File.Delete(wrapped);
                }
            }
            index++;
        }
        // The controller is retained. Sample every manual choice and the authored automatic mode.
        string? mode = plan["daytime_split"]?["video_selection"]?["mode_property"]?.GetValue<string>();
        string? manual = plan["daytime_split"]?["video_selection"]?["manual_property"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(mode) || string.IsNullOrWhiteSpace(manual))
            throw new InvalidDataException("Daytime selection properties are missing.");
        var comparisons = new JsonArray();
        HybridAnalyzeRequest settings = PlanSettings.Of(plan);
        for (int selection = 0; selection <= 5; selection++)
        {
            JsonObject properties = plan["snapshot_properties"]?.DeepClone().AsObject() ?? new JsonObject();
            if (selection < 5)
            {
                properties[mode] = false;
                properties[manual] = selection.ToString(CultureInfo.InvariantCulture);
            }
            PairedComparison paired = await new CandidateValidation(tools).CompareAsync(new ValidationRequest(1,
                source.SourcePath, project, settings.Assets, Path.Combine(layout.Output, $"selection-{selection}"),
                settings.Width, settings.Height, settings.FpsNumerator, settings.FpsDenominator,
                CompositionGate.RequiredFrames, DeviceUuid: request.DeviceUuid, UserProperties: properties), progress, token);
            JsonObject verdict = CompositionGate.Evaluate(paired);
            comparisons.Add(new JsonObject { ["selection"] = selection == 5 ? "automatic" : selection.ToString(CultureInfo.InvariantCulture),
                ["validation"] = verdict });
            if (verdict["status"]?.GetValue<string>() != "composition_pass")
            {
                report["status"] = "candidate_rejected_composition";
                report["composition_validation"] = new JsonObject { ["status"] = "composition_rejected", ["selections"] = comparisons };
                await BakeReportWriter.SaveAsync(layout.Report, report, timing, CancellationToken.None);
                return report;
            }
        }
        report["composition_validation"] = new JsonObject { ["status"] = "composition_pass", ["selections"] = comparisons };
        report["source_video_optimization"]!["status"] = "validated";
        report["status"] = "candidate_generated";
        await ProjectPublisher.PublishAsync(report, project, destination, layout, timing, progress, token);
        await BakeReportWriter.SaveAsync(layout.Report, report, timing, token);
        return report;
    }

    private static async Task<string> PacketTimelineAsync(FfmpegTool ff, NativeTools tools, string path, string stream,
        string log, CancellationToken token)
    {
        string output = await ff.RunTextAsync(tools.Ffprobe,
            ["-v", "error", "-select_streams", stream, "-show_packets", "-show_entries",
             stream == "a:0" ? "packet=pts_time,duration_time,size,data_hash" : "packet=pts_time,duration_time",
             "-show_data_hash", "sha256", "-of", "json", path], log, token);
        JsonArray packets = JsonNode.Parse(output)?["packets"] as JsonArray ?? [];
        if (packets.Count == 0) throw new InvalidDataException("A selected video/audio stream has no packets.");
        // H.264 can reorder packet emission; presentation timestamps and durations must still be identical.
        return string.Join('\n', packets.OfType<JsonObject>().Select(packet => packet.ToJsonString()).Order(StringComparer.Ordinal));
    }

    internal static async Task<JsonObject?> TryPlanAsync(JsonObject plan, NativeTools tools, CancellationToken token)
    {
        if (plan["route"]?.GetValue<string>() != "whole_layer" || plan["status"]?.GetValue<string>() != "requires_resolution" ||
            plan["source"]?.GetValue<string>() is not string sourcePath ||
            plan["runtime_evidence"]?.GetValue<string>() is not string runtimePath || !File.Exists(runtimePath) ||
            plan["projection"]?["status"]?.GetValue<string>() != "orthographic" ||
            plan["has_parallax"]?.GetValue<bool>() == true) return null;
        HybridAnalyzeRequest settings = PlanSettings.Of(plan);
        if (settings.Width < 2 || settings.Height < 2 || (settings.Width & 1) != 0 || (settings.Height & 1) != 0) return null;
        using var source = new ProjectSource(sourcePath);
        JsonObject scene = source.ReadJson(source.SceneResource);
        var objects = scene["objects"]!.AsArray().OfType<JsonObject>().ToDictionary(SceneGraph.Id);
        JsonObject runtime = JsonNode.Parse(await File.ReadAllTextAsync(runtimePath, token))!.AsObject();
        JsonArray dependencies = runtime["runtime_dependencies"]?.AsArray() ?? [];
        DaytimeSplit.Detection detection = DaytimeSplit.Detect(objects, dependencies, plan["snapshot_properties"] as JsonObject);
        if (!detection.IsRecognized || !detection.ControlsVideoPlayback || detection.Selection is null ||
            detection.States.Length < 2 || detection.Selection.LayerIds.Length <= detection.States.Length) return null;
        var layers = (plan["layers"] as JsonArray ?? []).OfType<JsonObject>().ToDictionary(SceneGraph.Id);
        JsonObject[] observed = [.. (runtime["runtime_layers"] as JsonArray ?? []).OfType<JsonObject>()];
        var lookups = Liveness.ScriptLookupEdges(objects);
        double canvasWidth = SceneGraph.Numeric(plan["projection"]?["canvas_width"], double.NaN);
        double canvasHeight = SceneGraph.Numeric(plan["projection"]?["canvas_height"], double.NaN);
        if (!double.IsFinite(canvasWidth * canvasHeight) || canvasWidth <= 0 || canvasHeight <= 0 ||
            Math.Abs(canvasWidth * settings.Height - canvasHeight * settings.Width) > 1e-6) return null;

        var resources = new JsonArray();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string probe = Path.Combine(plan["analysis_directory"]!.GetValue<string>(), "source-video-probe");
        Directory.CreateDirectory(probe);
        try
        {
            int index = 0;
            foreach (int id in detection.Selection.LayerIds)
            {
                token.ThrowIfCancellationRequested();
                if (!objects.TryGetValue(id, out JsonObject? layer) || !layers.TryGetValue(id, out JsonObject? described) ||
                    !SimpleLeaf(layer, objects, described, canvasWidth, canvasHeight, settings.Width, settings.Height) ||
                    lookups.Any(edge => SceneGraph.Int(edge["target"]) == id && SceneGraph.Int(edge["owner"]) != detection.ControllerId) ||
                    dependencies.OfType<JsonObject>().Any(edge => SceneGraph.Int(edge["target"]) == id &&
                        edge["operation"]?.GetValue<string>() is "read" or "write" && !SelectorAccess(edge, detection.ControllerId!.Value, id)))
                    continue;
                JsonObject model, material;
                try
                {
                    model = source.ReadJson(layer["image"]!.GetValue<string>());
                    material = source.ReadJson(model["material"]!.GetValue<string>());
                }
                catch (Exception error) when (error is IOException or InvalidDataException) { continue; }
                if (SceneGraph.Int(model["width"]) != canvasWidth || SceneGraph.Int(model["height"]) != canvasHeight ||
                    model["nopadding"]?.GetValue<bool>() != true || material["passes"] is not JsonArray { Count: 1 } passes ||
                    passes[0] is not JsonObject pass || pass["shader"]?.GetValue<string>() != "genericimage4" ||
                    pass["textures"] is not JsonArray { Count: 1 } textures || textures[0]?.GetValue<string>() is not string key ||
                    string.IsNullOrWhiteSpace(key) || !claimed.Add(key)) continue;
                string resource;
                try { resource = ProjectSource.NormalizeResource("materials/" + key + ".tex"); }
                catch (InvalidDataException) { continue; }
                if (!source.Contains(resource) || !UniqueRuntimeUse(observed, id, key) ||
                    !UniqueStaticUse(source, objects, id, model["material"]!.GetValue<string>(), key)) continue;
                string tex = Path.Combine(probe, index.ToString(CultureInfo.InvariantCulture) + ".tex");
                string mp4 = Path.Combine(probe, index.ToString(CultureInfo.InvariantCulture) + ".mp4");
                try
                {
                    await source.CopyResourceAsync(resource, tex, token);
                    TextureContainer.TextureHeader header = await TextureContainer.ExtractVideoAsync(tex, mp4, token);
                    JsonObject? media = await MediaAsync(tools, mp4, Path.Combine(probe, index + ".ffprobe.log"), token);
                    if (media is null || media["width"]!.GetValue<int>() != header.Width ||
                        media["height"]!.GetValue<int>() != header.Height ||
                        WorkloadValue.DecodeWorkStatus(settings.Width, settings.Height, media["fps"]!.GetValue<double>(),
                            header.Width, header.Height, media["fps"]!.GetValue<double>()) != WorkloadValue.DecodePotentialGain)
                        continue;
                    resources.Add(new JsonObject { ["layer_id"] = id, ["resource"] = resource,
                        ["source_width"] = header.Width, ["source_height"] = header.Height,
                        ["target_width"] = settings.Width, ["target_height"] = settings.Height,
                        ["codec"] = "h264", ["frame_count"] = media["frame_count"]!.DeepClone(),
                        ["fps_num"] = media["fps_num"]!.DeepClone(), ["fps_den"] = media["fps_den"]!.DeepClone(),
                        ["audio_streams"] = media["audio_streams"]!.DeepClone() });
                }
                catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException) { }
                finally
                {
                    if (File.Exists(tex)) File.Delete(tex);
                    if (File.Exists(mp4)) File.Delete(mp4);
                }
                index++;
            }
        }
        finally { Directory.Delete(probe, recursive: true); }
        if (resources.Count == 0 || !resources.OfType<JsonObject>().Any(resource =>
            detection.States.Any(state => state.VisibleLayerIds.Contains(resource["layer_id"]!.GetValue<int>())))) return null;

        plan["route"] = Route;
        plan["status"] = "source_video_resources_ready";
        plan["daytime_split"] = detection.ToJson();
        plan["source_video_optimization"] = new JsonObject { ["status"] = "planned", ["resources"] = resources,
            ["scope"] = "The original scene, layer IDs, scripts, materials and playback selector stay in place; only eligible video TEX bodies are resized. No new video streams or analytic loop are claimed." };
        plan["blockers"] = new JsonArray();
        plan["blockers_localized"] = new JsonArray();
        plan.Remove(NoBenefit.Field);
        plan.Remove(TradeoffOptions.Field);
        plan["bake_value"] = new JsonObject { ["status"] = WorkloadValue.DecodePotentialGain,
            ["rule"] = WorkloadValue.ReducedVideoDecode.Rule,
            ["reason_zh"] = WorkloadValue.ReducedVideoDecode.ReasonZh,
            ["reason_en"] = WorkloadValue.ReducedVideoDecode.ReasonEn,
            ["evidence"] = new JsonObject { ["resource_count"] = resources.Count,
                ["basis"] = "Each MP4 stream was probed without decoding all frames; transcoding preserves its frame rate and frame count. This is potential per-resource decoder work, not a power measurement or simultaneous-decoder total." },
            ["device_independent"] = true, ["scope"] = WorkloadValue.Scope };
        plan["suitability"] = new JsonObject { ["verdict"] = "bakeable", ["rule"] = Route };
        plan["summary"] = new JsonObject { ["key"] = "summary.source_video_optimization",
            ["zh"] = $"可以保持昼夜与手动切换，并把 {resources.Count} 段原视频缩到本次输出尺寸；实际省电仍需对照。",
            ["en"] = $"Ready to resize {resources.Count} authored videos while preserving daytime and manual switching; power savings remain unmeasured." };
        return plan;
    }

    private static bool SelectorAccess(JsonObject edge, int controller, int target) =>
        SceneGraph.Int(edge["owner"]) == controller && SceneGraph.Int(edge["target"]) == target &&
        edge["binding"]?.GetValue<string>() == "visible" &&
        ((edge["operation"]?.GetValue<string>() == "read" && edge["property"]?.GetValue<string>() == "videoTexture") ||
         (edge["operation"]?.GetValue<string>() == "write" && edge["property"]?.GetValue<string>() == "visible"));

    private static bool SimpleLeaf(JsonObject layer, IReadOnlyDictionary<int, JsonObject> objects, JsonObject described,
        double canvasWidth, double canvasHeight, uint outputWidth, uint outputHeight)
    {
        int id = SceneGraph.Id(layer);
        if (layer["image"] is not JsonValue || layer["effects"] is JsonArray { Count: > 0 } ||
            objects.Values.Any(obj => SceneGraph.Int(obj["parent"]) == id) ||
            SceneAnalyzer.Walk(layer).OfType<JsonObject>().Any(node => node["script"] is not null) ||
            SceneGraph.Numeric(described["canvas_fraction"], 0) != 1 ||
            Math.Abs(SceneGraph.Numeric(described["canvas_center_x"], double.NaN) - .5) > 1e-6 ||
            Math.Abs(SceneGraph.Numeric(described["canvas_center_y"], double.NaN) - .5) > 1e-6) return false;
        try
        {
            var size = HybridVideoProjection.Vector(layer["size"], (0, 0));
            var scale = HybridVideoProjection.Vector(layer["scale"], (1, 1));
            JsonObject? inherited = SceneGraph.Int(layer["parent"]) is int parent && objects.ContainsKey(parent)
                ? HybridVideoProjection.ParentTransform(objects, parent) : null;
            var parentScale = HybridVideoProjection.Vector(inherited?["scale"], (1, 1));
            if (Math.Abs(size.X * scale.X * parentScale.X - canvasWidth) > 1e-3 ||
                Math.Abs(size.Y * scale.Y * parentScale.Y - canvasHeight) > 1e-3 ||
                Math.Abs(canvasWidth * outputHeight - canvasHeight * outputWidth) > 1e-6) return false;
            for (int? cursor = id; cursor is int current && objects.TryGetValue(current, out JsonObject? obj);
                cursor = SceneGraph.Int(obj["parent"]))
                if (new[] { "origin", "scale", "angles", "size" }.Any(key => obj[key] is JsonObject)) return false;
            return true;
        }
        catch (InvalidDataException) { return false; }
    }

    private static bool UniqueRuntimeUse(JsonObject[] observed, int owner, string key)
    {
        JsonObject[] uses = [.. observed.Where(layer => (layer["materials"] as JsonArray ?? []).OfType<JsonObject>()
            .Any(material => (material["textures"] as JsonArray ?? []).Any(texture => texture?.GetValue<string>() == key)))];
        return uses.Length == 1 && SceneGraph.Int(uses[0]["owner"]) == owner &&
            uses[0]["has_effect_layer"]?.GetValue<bool>() == false &&
            uses[0]["materials"] is JsonArray { Count: 1 } materials &&
            materials[0]?["role"]?.GetValue<string>() == "source";
    }

    internal static bool UniqueStaticUse(ProjectSource source, IReadOnlyDictionary<int, JsonObject> objects,
        int owner, string materialResource, string key)
    {
        // Runtime traces only cover the current property state. A hidden layer or effect may sample
        // the same resource after a user switch, so require one authored material use as well.
        int references = 0;
        try
        {
            foreach (string path in source.ResourceNames().Where(path =>
                path.StartsWith("materials/", StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            {
                JsonObject material = source.ReadJson(path);
                foreach (JsonValue value in SceneAnalyzer.Walk(material).OfType<JsonValue>())
                    if (value.TryGetValue<string>(out string? text) && text == key) references++;
                if (references > 1) return false;
            }
            if (references != 1) return false;
            foreach (var (id, layer) in objects)
            {
                if (layer["image"] is not JsonValue imageValue ||
                    !imageValue.TryGetValue<string>(out string? image) || image is null) continue;
                if (!source.Contains(image)) continue; // Built-in renderer models cannot refer to an authored material path.
                JsonObject model = source.ReadJson(image);
                if (model["material"]?.GetValue<string>() == materialResource && id != owner) return false;
            }
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException)
        { return false; }
    }

    private static async Task<JsonObject?> MediaAsync(NativeTools tools, string path, string log, CancellationToken token)
    {
        string output = await new FfmpegTool(tools).RunTextAsync(tools.Ffprobe,
            ["-v", "error", "-show_entries", "stream=codec_type,codec_name,width,height,pix_fmt,color_space,color_transfer,color_primaries,avg_frame_rate,nb_frames,duration,start_time", "-of", "json", path],
            log, token);
        JsonArray streams = JsonNode.Parse(output)?["streams"] as JsonArray ?? [];
        JsonObject[] video = [.. streams.OfType<JsonObject>().Where(stream => stream["codec_type"]?.GetValue<string>() == "video")];
        JsonObject[] audio = [.. streams.OfType<JsonObject>().Where(stream => stream["codec_type"]?.GetValue<string>() == "audio")];
        if (video.Length != 1 || audio.Length > 1 || streams.Count != video.Length + audio.Length ||
            video[0]["codec_name"]?.GetValue<string>() != "h264" || video[0]["pix_fmt"]?.GetValue<string>() != "yuv420p" ||
            video[0]["color_space"]?.GetValue<string>() != "bt709" ||
            video[0]["color_transfer"]?.GetValue<string>() != "bt709" ||
            video[0]["color_primaries"]?.GetValue<string>() != "bt709" ||
            audio.Any(stream => stream["codec_name"]?.GetValue<string>() != "aac") ||
            video[0]["width"]?.GetValue<int>() is not int width || video[0]["height"]?.GetValue<int>() is not int height ||
            video[0]["nb_frames"]?.GetValue<string>() is not string count || !ulong.TryParse(count, out ulong frames) || frames == 0 ||
            video[0]["avg_frame_rate"]?.GetValue<string>() is not string rate || rate.Split('/') is not [string numerator, string denominator] ||
            !uint.TryParse(numerator, out uint fpsNum) || !uint.TryParse(denominator, out uint fpsDen) || fpsNum == 0 || fpsDen == 0 ||
            video[0]["start_time"]?.GetValue<string>() != "0.000000") return null;
        return new JsonObject { ["width"] = width, ["height"] = height, ["frame_count"] = frames,
            ["fps_num"] = fpsNum, ["fps_den"] = fpsDen, ["fps"] = (double)fpsNum / fpsDen,
            ["audio_streams"] = audio.Length };
    }
}
