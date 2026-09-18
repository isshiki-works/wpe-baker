using System.Text.Json.Nodes;

namespace Baker.Core;

public sealed record EffectAnalysis(int ObjectId, string ObjectName, int EffectIndex, int? EffectId,
    string Resource, int PassCount, string Decision, string[] Reasons, string[] Bindings);
public sealed record SceneAnalysis(int SchemaVersion, string Source, string SourceSha256, string Kind,
    int? PackageVersion, string Status, int ObjectCount, int ScriptCount, string[] LiveFeatures,
    EffectAnalysis[] Effects, string[] Diagnostics, string SourceDigestScope = ProjectSource.DigestScope);

public static class SceneAnalyzer
{
    public static async Task<SceneAnalysis> AnalyzeAsync(string path, string? assetsDirectory = null,
        CancellationToken cancellationToken = default)
    {
        using var source = new ProjectSource(path);
        string hash = await source.SourceHashAsync(cancellationToken);
        if (source.Kind != "scene")
            return new(1, source.SourcePath, hash, source.Kind, source.PackageVersion, "metadata_only", 0, 0,
                [], [], ["Media optimization requires inspection of the actual codecs and page resource dependencies."]);
        var scene = source.ReadJson(source.SceneResource);
        var objects = scene["objects"]?.AsArray() ?? throw new InvalidDataException("Scene has no object array.");
        var diagnostics = new List<string>();
        var live = new HashSet<string>();
        int scripts = 0;
        var effects = new List<EffectAnalysis>();
        foreach (var node in Walk(scene))
        {
            if (node is not JsonObject binding) continue;
            if (binding["script"] is JsonValue scriptValue && scriptValue.TryGetValue<string>(out var script))
            {
                ++scripts;
                if (script.Contains("registerAudioBuffers", StringComparison.Ordinal)) live.Add("audio_response");
                if (script.Contains("createLayer", StringComparison.Ordinal) || script.Contains("sortLayer", StringComparison.Ordinal)) live.Add("dynamic_layers_and_order");
                if (script.Contains("Date", StringComparison.Ordinal)) live.Add("clock_or_date");
                if (script.Contains("cursor", StringComparison.OrdinalIgnoreCase) || script.Contains("mouse", StringComparison.OrdinalIgnoreCase)) live.Add("script_input");
            }
            if (binding.ContainsKey("user")) live.Add("user_properties");
        }
        foreach (var node in objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node is not JsonObject obj) throw new InvalidDataException("Scene object must be an object.");
            int objectId = obj["id"]?.GetValue<int>() ?? -1;
            string name = obj["name"]?.GetValue<string>() ?? $"Object {objectId}";
            if (obj.ContainsKey("particle")) live.Add("particles");
            if (obj.ContainsKey("sound")) live.Add("authored_audio");
            if (obj.ContainsKey("text")) live.Add("text");
            if (obj["parallaxDepth"] is { } depth && depth.ToJsonString().Any(c => c is >= '1' and <= '9')) live.Add("mouse_parallax");
            if (obj["effects"] is not JsonArray authoredEffects) continue;
            for (int i = 0; i < authoredEffects.Count; ++i)
            {
                var effect = authoredEffects[i]?.AsObject() ?? throw new InvalidDataException("Invalid effect entry.");
                string resource = effect["file"]?.GetValue<string>() ?? "";
                var reasons = new List<string>();
                var bindings = Walk(effect).OfType<JsonObject>()
                    .SelectMany(o => o.ContainsKey("script") ? new[] { "script" } : o["user"] is { } user ? new[] { user.ToJsonString() } : [])
                    .Distinct().ToArray();
                string decision = "retain_live";
                int count = 0;
                try
                {
                    var definition = ReadResourceJson(source, assetsDirectory, resource);
                    var passes = definition["passes"]?.AsArray() ?? throw new InvalidDataException("Effect has no pass list.");
                    count = passes.Count;
                    bool background = Walk(definition).OfType<JsonValue>().Any(v => v.TryGetValue<string>(out var s) &&
                        (s.Contains("_rt_FullFrameBuffer", StringComparison.Ordinal) || s.Contains("_rt_Backbuffer", StringComparison.Ordinal)));
                    if (bindings.Length > 0) reasons.Add("Effect has script or property bindings; continuous controls must remain live or be factored after the cache.");
                    if (background) reasons.Add("Effect samples the scene background; cache cannot cross that dependency.");
                    bool shine = passes.Count == 5 &&
                        passes.Select(p => p?["material"]?.GetValue<string>()).SequenceEqual(new[] {
                            "materials/effects/shine_downsample2.json", "materials/effects/shine_cast.json",
                            "materials/effects/shine_gaussian_x.json", "materials/effects/shine_gaussian_y.json", "materials/effects/shine_combine.json" });
                    if (shine && bindings.Length == 0 && !background)
                    {
                        decision = "probe_intermediate_cache";
                        reasons.Add("Four half-resolution passes precede the original full-resolution combine; preserve original object, final combine and all later effects.");
                        reasons.Add("Candidate only: shader dependencies, input stability, loop seam, decoder cost and official playback still require validation.");
                    }
                    else if (reasons.Count == 0) reasons.Add("No validated transformation rule for this effect; it remains live.");
                }
                catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException)
                {
                    reasons.Add($"Effect metadata unavailable: {error.Message}");
                    diagnostics.Add($"Object {objectId}, effect {i}: {error.GetType().Name}: {error.Message}");
                }
                effects.Add(new(objectId, name, i, effect["id"]?.GetValue<int>(), resource, count, decision, reasons.ToArray(), bindings));
            }
        }
        if (scripts > 0) diagnostics.Add("Scripts are preserved verbatim. Static inspection does not certify arbitrary script side effects or API compatibility.");
        return new(1, source.SourcePath, hash, source.Kind, source.PackageVersion, "analyzed_not_validated", objects.Count,
            scripts, live.Order().ToArray(), effects.ToArray(), diagnostics.ToArray());
    }

    public static JsonObject ReadResourceJson(ProjectSource source, string? assetsDirectory, string resource)
    {
        if (source.Contains(resource)) return source.ReadJson(resource);
        if (assetsDirectory is null) throw new FileNotFoundException("Resource is not included in source; specify the official assets directory.", resource);
        string path = ProjectSource.ContainedPath(assetsDirectory, resource);
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("JSON resource exceeds metadata limit.");
        return ProjectSource.ParseWpeJsonObject(File.ReadAllBytes(path), path);
    }

    internal static IEnumerable<JsonNode> Walk(JsonNode? node)
    {
        if (node is null) yield break;
        yield return node;
        if (node is JsonObject obj)
            foreach (var pair in obj) foreach (var child in Walk(pair.Value)) yield return child;
        else if (node is JsonArray array)
            foreach (var value in array) foreach (var child in Walk(value)) yield return child;
    }
}
