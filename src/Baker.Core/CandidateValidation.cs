using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baker.Core;

public sealed record ValidationRequest(int SchemaVersion, string Source, string Candidate, string Assets,
    string OutputDirectory, uint Width, uint Height, uint FpsNumerator = 60, uint FpsDenominator = 1,
    ulong Frames = 24, ulong WarmupFrames = 0, ulong Seed = 0, string? DeviceUuid = null,
    JsonObject? UserProperties = null, JsonObject? Input = null, JsonArray? InputTimeline = null, uint TileSize = 64,
    bool RetainRawFrames = true);

/// <summary>Measures sampled offline RGBA differences; never certifies visual equivalence.</summary>
public sealed class CandidateValidation(NativeTools tools)
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public async Task<JsonObject> ValidateAsync(ValidationRequest request, IProgress<RenderProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (request.SchemaVersion != 1 || request.Frames == 0 || request.FpsNumerator == 0 ||
            request.FpsDenominator == 0 || request.TileSize == 0 || request.TileSize > ushort.MaxValue)
            throw new ArgumentException("Validation requires schema version 1 and positive frame, FPS and tile values.");
        int frameBytes = FrameByteCount(request.Width, request.Height);
        string output = PrepareOutput(request.Source, request.Candidate, request.OutputDirectory);
        string reportPath = Path.Combine(output, "comparison.json");
        string framesPath = Path.Combine(output, "frame-comparison.jsonl");
        var report = new JsonObject
        {
            ["schema_version"] = 1, ["status"] = "running", ["report_path"] = reportPath,
            ["request"] = JsonSerializer.SerializeToNode(request, JsonOptions),
            ["scope"] = "Sampled full-frame RGBA8 from the same offline renderer and inputs; RGB includes transparent pixels.",
            ["automatic_visual_certification"] = false, ["official_playback_verified"] = false,
            ["frames_report_path"] = framesPath, ["raw_retained"] = true
        };
        await WriteReportAsync(reportPath, report, cancellationToken);
        bool validationFailed = false;
        try
        {
            var runner = new NativeRenderRunner(tools);
            RenderRequest RenderSide(string source, string label) => new(source, request.Assets, Path.Combine(output, label),
                request.Width, request.Height, request.FpsNumerator, request.FpsDenominator, request.Frames,
                request.WarmupFrames, request.Seed, DeviceUuid: request.DeviceUuid, UserProperties: request.UserProperties,
                Input: request.Input, InputTimeline: request.InputTimeline, TraceScene: true);
            progress?.Report(new("rendering_source", 0, "Rendering sampled source frames."));
            JsonObject original = await runner.RenderRawAsync(RenderSide(request.Source, "source"), cancellationToken);
            progress?.Report(new("rendering_candidate", 0, "Rendering the candidate with the same sampled inputs."));
            JsonObject candidate = await runner.RenderRawAsync(RenderSide(request.Candidate, "candidate"), cancellationToken);
            string aPath = original["rgba_path"]!.GetValue<string>();
            string bPath = candidate["rgba_path"]!.GetValue<string>();
            report["source_raw_path"] = aPath;
            report["candidate_raw_path"] = bPath;
            report["source_sha256"] = original["source_sha256"]!.DeepClone();
            report["candidate_sha256"] = candidate["source_sha256"]!.DeepClone();
            report["source_native_result"] = original["native_result"]!.DeepClone();
            report["candidate_native_result"] = candidate["native_result"]!.DeepClone();
            report["source_compiled_scene_passes"] = original["native_result"]!["compiled_scene_passes"]!.DeepClone();
            report["candidate_compiled_scene_passes"] = candidate["native_result"]!["compiled_scene_passes"]!.DeepClone();
            int width = checked((int)request.Width), height = checked((int)request.Height), edge = checked((int)request.TileSize);
            int columns = (width + edge - 1) / edge, rows = (height + edge - 1) / edge;
            var aggregateTiles = Enumerable.Range(0, checked(columns * rows)).Select(_ => new Errors()).ToArray();
            var aggregate = new Errors();
            byte[] a = new byte[frameBytes], b = new byte[frameBytes];
            await using var aFile = new FileStream(aPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
            await using var bFile = new FileStream(bPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
            await using var frameReport = new StreamWriter(new FileStream(framesPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read, 128 * 1024, true), new UTF8Encoding(false));
            for (ulong frame = 0; frame < request.Frames; ++frame)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await aFile.ReadExactlyAsync(a, cancellationToken);
                await bFile.ReadExactlyAsync(b, cancellationToken);
                var frameErrors = new Errors();
                for (int y = 0; y < height; ++y)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int tileRow = y / edge * columns;
                    for (int x = 0; x < width; ++x)
                    {
                        int offset = (y * width + x) * 4;
                        int dr = Math.Abs(a[offset] - b[offset]), dg = Math.Abs(a[offset + 1] - b[offset + 1]);
                        int db = Math.Abs(a[offset + 2] - b[offset + 2]), da = Math.Abs(a[offset + 3] - b[offset + 3]);
                        frameErrors.Add(dr, dg, db, da);
                        aggregateTiles[tileRow + x / edge].Add(dr, dg, db, da);
                    }
                }
                aggregate.Merge(frameErrors);
                var line = new JsonObject { ["frame"] = frame, ["metrics"] = frameErrors.ToJson() };
                await frameReport.WriteLineAsync(line.ToJsonString().AsMemory(), cancellationToken);
                progress?.Report(new("comparing", (double)(frame + 1) / request.Frames, $"Compared {frame + 1} / {request.Frames} frames."));
            }
            using var sourceView = new ProjectSource(request.Source);
            using var candidateView = new ProjectSource(request.Candidate);
            if (await sourceView.SourceHashAsync(cancellationToken) != original["source_sha256"]!.GetValue<string>() ||
                await candidateView.SourceHashAsync(cancellationToken) != candidate["source_sha256"]!.GetValue<string>())
                throw new IOException("Source or candidate changed during the paired comparison.");
            report["object_preservation"] = ObjectPreservation(sourceView, candidateView);
            report["lookup_binding_validation"] = CompareLookupBindings(
                sourceView.ReadJson(sourceView.SceneResource)["objects"]!.AsArray(),
                candidateView.ReadJson(candidateView.SceneResource)["objects"]!.AsArray(),
                original["native_result"]?["runtime_dependencies"] as JsonArray,
                candidate["native_result"]?["runtime_dependencies"] as JsonArray);
            report["frames_compared"] = request.Frames;
            report["metrics"] = aggregate.ToJson();
            report["tiles"] = Tiles(aggregateTiles, columns, edge, width, height);
            int worst = Enumerable.Range(0, aggregateTiles.Length).MaxBy(i => aggregateTiles[i].RgbSum / aggregateTiles[i].Pixels);
            report["worst_rgb_tile"] = report["tiles"]![worst]!.DeepClone();
            report["byte_identical_samples"] = aggregate.MaxRgb == 0 && aggregate.MaxAlpha == 0;
            report["status"] = "compared";
            await WriteReportAsync(reportPath, report, cancellationToken);
            return report;
        }
        catch (Exception error)
        {
            validationFailed = true;
            report["status"] = cancellationToken.IsCancellationRequested ? "cancelled" : "failed";
            report["error_type"] = error.GetType().Name;
            report["error"] = error.Message;
            await WriteReportAsync(reportPath, report, CancellationToken.None);
            throw;
        }
        finally
        {
            if (!request.RetainRawFrames)
            {
                string[] rawFiles = [
                    "source/native/frames.rgba", "source/native/frames.rgba.partial",
                    "source/native/audio.f32le", "source/native/audio.f32le.partial",
                    "candidate/native/frames.rgba", "candidate/native/frames.rgba.partial",
                    "candidate/native/audio.f32le", "candidate/native/audio.f32le.partial"];
                TemporaryCaptureFiles.Delete(report, output, rawFiles);
                report["raw_retained"] = rawFiles.Any(relative => File.Exists(Path.Combine(output, relative)));
                try { await WriteReportAsync(reportPath, report, CancellationToken.None); }
                catch when (validationFailed) { }
            }
        }
    }

    internal static JsonObject CompareLookupBindings(JsonArray sourceObjects, JsonArray candidateObjects,
        JsonArray? sourceDependencies, JsonArray? candidateDependencies)
    {
        var mismatches = new JsonArray();
        var report = new JsonObject
        {
            ["status"] = "not_available", ["mismatches"] = mismatches,
            ["scope"] = "Lookup target sets of retained authored script owners in this paired sample only; unexecuted branches and dynamic-object identity are not certified."
        };
        if (sourceDependencies is null || candidateDependencies is null) return report;
        try
        {
            HybridExportSafety.GuardPublicLayerQueries(sourceObjects.OfType<JsonObject>(), candidateObjects.OfType<JsonObject>(),
                HybridExportSafety.MergeRuntimeDependencies(sourceDependencies, candidateDependencies));
        }
        catch (InvalidDataException error)
        {
            report["status"] = "rejected_public_layer_queries";
            report["reason"] = error.Message;
            return report;
        }
        var sourceIds = sourceObjects.OfType<JsonObject>().Where(obj => Scripts(obj).Count > 0)
            .Select(HybridScenePlanner.Id).ToHashSet();
        var retained = candidateObjects.OfType<JsonObject>()
            .Where(obj => sourceIds.Contains(HybridScenePlanner.Id(obj)) && Scripts(obj).Count > 0)
            .Select(HybridScenePlanner.Id).ToHashSet();
        Dictionary<(int Owner, string Binding, bool Initialization, string Property), HashSet<int>> Lookups(JsonArray dependencies)
        {
            var result = new Dictionary<(int, string, bool, string), HashSet<int>>();
            foreach (var dependency in dependencies.OfType<JsonObject>())
            {
                if (dependency["operation"]?.GetValue<string>() != "lookup" ||
                    HybridScenePlanner.Int(dependency["owner"]) is not int owner || !retained.Contains(owner)) continue;
                var key = (owner, dependency["binding"]?.GetValue<string>() ?? "",
                    dependency["initialization"]?.GetValue<bool>() == true, dependency["property"]?.GetValue<string>() ?? "");
                if (!result.TryGetValue(key, out var targets)) result.Add(key, targets = []);
                targets.Add(HybridScenePlanner.Int(dependency["target"]) ?? -1);
            }
            return result;
        }
        var original = Lookups(sourceDependencies);
        var candidate = Lookups(candidateDependencies);
        foreach (var key in original.Keys.Union(candidate.Keys).OrderBy(key => key.Owner)
            .ThenBy(key => key.Binding, StringComparer.Ordinal).ThenBy(key => key.Initialization)
            .ThenBy(key => key.Property, StringComparer.Ordinal))
        {
            var a = original.GetValueOrDefault(key) ?? [];
            var b = candidate.GetValueOrDefault(key) ?? [];
            if (a.SetEquals(b)) continue;
            mismatches.Add(new JsonObject
            {
                ["owner"] = key.Owner, ["binding"] = key.Binding, ["initialization"] = key.Initialization,
                ["property"] = key.Property, ["source_targets"] = JsonSerializer.SerializeToNode(a.Order()),
                ["candidate_targets"] = JsonSerializer.SerializeToNode(b.Order())
            });
        }
        report["retained_script_owner_count"] = retained.Count;
        report["source_lookup_binding_count"] = original.Count;
        report["candidate_lookup_binding_count"] = candidate.Count;
        report["status"] = mismatches.Count == 0 ? "observed_lookups_match" : "rejected_lookup_bindings";
        return report;
    }

    internal static int FrameByteCount(uint width, uint height)
    {
        ulong bytes = (ulong)width * height * 4;
        if (width == 0 || height == 0 || width > ushort.MaxValue || height > ushort.MaxValue || bytes > 256ul * 1024 * 1024)
            throw new ArgumentException("RGBA extent must be positive and fit the native 256 MiB frame budget.");
        return checked((int)bytes);
    }

    internal static string PrepareOutput(string original, string candidate, string requested)
    {
        string output = Path.GetFullPath(requested);
        ProjectSource.EnsureNoReparsePoints(output);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Paired output must be a new directory.");
        foreach (string input in new[] { original, candidate })
        {
            using var source = new ProjectSource(input);
            if (source.Kind != "scene") throw new InvalidDataException("Paired rendering requires scene projects.");
            string prefix = Path.TrimEndingDirectorySeparator(source.DirectoryPath) + Path.DirectorySeparatorChar;
            if (output.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Paired output cannot be inside either input project.");
        }
        Directory.CreateDirectory(output);
        return output;
    }

    internal static Task WriteReportAsync(string path, JsonObject report, CancellationToken token) =>
        File.WriteAllTextAsync(path, report.ToJsonString(JsonOptions), token);

    private sealed class Errors
    {
        public ulong Pixels, DifferentRgb, DifferentAlpha;
        public double RgbSum, RgbSquared, AlphaSum;
        public int MaxRgb, MaxAlpha;
        public void Add(int r, int g, int b, int alpha)
        {
            ++Pixels;
            RgbSum += r + g + b;
            RgbSquared += r * r + g * g + b * b;
            AlphaSum += alpha;
            int maximum = Math.Max(r, Math.Max(g, b));
            MaxRgb = Math.Max(MaxRgb, maximum); MaxAlpha = Math.Max(MaxAlpha, alpha);
            if (maximum != 0) ++DifferentRgb;
            if (alpha != 0) ++DifferentAlpha;
        }
        public void Merge(Errors other)
        {
            Pixels += other.Pixels; DifferentRgb += other.DifferentRgb; DifferentAlpha += other.DifferentAlpha;
            RgbSum += other.RgbSum; RgbSquared += other.RgbSquared; AlphaSum += other.AlphaSum;
            MaxRgb = Math.Max(MaxRgb, other.MaxRgb); MaxAlpha = Math.Max(MaxAlpha, other.MaxAlpha);
        }
        public JsonObject ToJson() => new()
        {
            ["pixels"] = Pixels, ["rgb_mae_255"] = RgbSum / (Pixels * 3.0),
            ["rgb_rmse_255"] = Math.Sqrt(RgbSquared / (Pixels * 3.0)), ["rgb_max_abs_255"] = (double)MaxRgb,
            ["alpha_mae_255"] = AlphaSum / Pixels, ["alpha_max_abs_255"] = (double)MaxAlpha,
            ["differing_rgb_pixel_fraction"] = (double)DifferentRgb / Pixels,
            ["differing_alpha_pixel_fraction"] = (double)DifferentAlpha / Pixels
        };
    }

    private static JsonArray Tiles(Errors[] errors, int columns, int edge, int width, int height)
    {
        var result = new JsonArray();
        for (int i = 0; i < errors.Length; ++i)
        {
            int x = i % columns * edge, y = i / columns * edge;
            result.Add(new JsonObject { ["x"] = x, ["y"] = y, ["width"] = Math.Min(edge, width - x),
                ["height"] = Math.Min(edge, height - y), ["metrics"] = errors[i].ToJson() });
        }
        return result;
    }

    private static JsonObject ObjectPreservation(ProjectSource source, ProjectSource candidate)
    {
        JsonObject aScene = source.ReadJson(source.SceneResource), bScene = candidate.ReadJson(candidate.SceneResource);
        JsonArray aObjects = aScene["objects"]?.AsArray() ?? throw new InvalidDataException("Source objects are missing.");
        JsonArray bObjects = bScene["objects"]?.AsArray() ?? throw new InvalidDataException("Candidate objects are missing.");
        int[] aIds = aObjects.Select(x => x?["id"]?.GetValue<int>() ?? -1).ToArray();
        int[] bIds = bObjects.Select(x => x?["id"]?.GetValue<int>() ?? -1).ToArray();
        var rows = new JsonArray();
        for (int i = 0; i < aIds.Length; ++i)
        {
            int[] matches = Enumerable.Range(0, bIds.Length).Where(j => bIds[j] == aIds[i]).ToArray();
            var row = new JsonObject { ["id"] = aIds[i], ["source_order"] = i,
                ["candidate_order"] = matches.Length == 1 ? JsonValue.Create(matches[0]) : null,
                ["state"] = matches.Length == 1 ? "present" : matches.Length == 0 ? "missing" : "ambiguous_id" };
            Dictionary<string, string> aScripts = Scripts(aObjects[i]);
            row["source_script_count"] = aScripts.Count;
            if (matches.Length == 1)
            {
                Dictionary<string, string> bScripts = Scripts(bObjects[matches[0]]);
                row["candidate_script_count"] = bScripts.Count;
                row["script_texts_equal"] = aScripts.Values.Order(StringComparer.Ordinal).SequenceEqual(bScripts.Values.Order(StringComparer.Ordinal));
                row["script_binding_paths_equal"] = aScripts.OrderBy(x => x.Key, StringComparer.Ordinal).SequenceEqual(bScripts.OrderBy(x => x.Key, StringComparer.Ordinal));
            }
            rows.Add(row);
        }
        Dictionary<string, string> allA = Scripts(aScene), allB = Scripts(bScene);
        return new JsonObject
        {
            ["scope"] = "Authored scene JSON IDs, order and script text/binding paths; runtime script behavior is not certified.",
            ["source_order"] = JsonSerializer.SerializeToNode(aIds), ["candidate_order"] = JsonSerializer.SerializeToNode(bIds),
            ["original_order_preserved"] = bIds.Where(aIds.Contains).SequenceEqual(aIds), ["objects"] = rows,
            ["source_script_count"] = allA.Count, ["candidate_script_count"] = allB.Count,
            ["script_texts_equal"] = allA.Values.Order(StringComparer.Ordinal).SequenceEqual(allB.Values.Order(StringComparer.Ordinal))
        };
    }

    private static Dictionary<string, string> Scripts(JsonNode? node)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        void Walk(JsonNode? current, string path)
        {
            if (current is JsonObject obj)
                foreach (var pair in obj)
                {
                    string child = path + "/" + pair.Key.Replace("~", "~0").Replace("/", "~1");
                    if (pair.Key == "script" && pair.Value is not null) found.Add(child, pair.Value.ToJsonString());
                    Walk(pair.Value, child);
                }
            else if (current is JsonArray array)
                for (int i = 0; i < array.Count; ++i) Walk(array[i], path + "/" + i);
        }
        Walk(node, "");
        return found;
    }
}
