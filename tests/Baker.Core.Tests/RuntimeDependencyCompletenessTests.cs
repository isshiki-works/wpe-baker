using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

[Trait("Layer", "L0")]
public class RuntimeDependencyCompletenessTests
{
    private const string CapDiagnostic = "Runtime dependency trace reached its 10000-entry limit";
    private static JsonObject Trace(int count = 0, bool? complete = true) => new()
    {
        ["status"] = "complete", ["runtime_layers"] = new JsonArray(),
        ["runtime_dependencies"] = new JsonArray([.. Enumerable.Range(0, count).Select(id => (JsonNode)new JsonObject {
            ["owner"] = id, ["target"] = 1, ["operation"] = "read", ["property"] = "origin", ["initialization"] = false })]),
        ["runtime_dependencies_complete"] = complete
    };
    private static JsonObject Legacy(int count = 0)
    {
        JsonObject trace = Trace(count);
        trace.Remove("runtime_dependencies_complete");
        return trace;
    }

    [Fact]
    public void ExplicitCompletenessAllowsTheCapButFalseOrMalformedEvidenceCannotBeConsumed()
    {
        Assert.Equal(10000, RuntimeObservation.RequireCompleteDependencies(Trace(10000)).Count);
        Assert.Throws<InvalidDataException>(() => RuntimeObservation.RequireCompleteDependencies(Trace(0, false)));
        Assert.Throws<InvalidDataException>(() => RuntimeObservation.RequireCompleteDependencies(Trace(0, null)));
        JsonObject failed = Trace();
        failed["status"] = "failed";
        Assert.Throws<InvalidDataException>(() => RuntimeObservation.RequireCompleteDependencies(failed));
    }

    [Fact]
    public void LegacyTraceBelowCapIsCompatibleButCapOrTruncationDiagnosticRequiresFreshEvidence()
    {
        Assert.Equal(9999, RuntimeObservation.RequireCompleteDependencies(Legacy(9999)).Count);
        var error = Assert.Throws<InvalidDataException>(() => RuntimeObservation.RequireCompleteDependencies(Legacy(10000)));
        Assert.Contains("collect a fresh trace", error.Message);
        JsonObject truncated = Legacy();
        truncated["diagnostics"] = new JsonArray(CapDiagnostic);
        Assert.Throws<InvalidDataException>(() => RuntimeObservation.RequireCompleteDependencies(truncated));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void OneIncompleteSegmentCannotBeHiddenByTheLastSegmentOrDependencyUnion(int incomplete)
    {
        JsonObject[] parts = [new() { ["native_result"] = Trace(1) }, new() { ["native_result"] = Trace(2) }];
        parts[incomplete]["native_result"]!["runtime_dependencies_complete"] = false;
        var combined = parts[^1]["native_result"]!.DeepClone().AsObject();
        NativeRenderRunner.MergeSegmentRuntimeDependencies(combined, parts);
        Assert.Equal(2, combined["runtime_dependencies"]!.AsArray().Count);
        Assert.False(combined["runtime_dependencies_complete"]!.GetValue<bool>());
        Assert.Throws<InvalidDataException>(() => HybridExportSafety.LateExternalDependencies(
            new JsonObject { ["native_result"] = combined }, new HashSet<int> { 1 }, new Dictionary<int, JsonObject>()));
    }

    [Fact]
    public void CompleteSegmentUnionBeyondThePerProcessCapRemainsUsable()
    {
        JsonObject second = Trace(1);
        second["runtime_dependencies"]![0]!["owner"] = 10000;
        JsonObject[] parts = [new() { ["native_result"] = Trace(10000) }, new() { ["native_result"] = second }];
        var combined = second.DeepClone().AsObject();
        NativeRenderRunner.MergeSegmentRuntimeDependencies(combined, parts);
        Assert.Equal(10001, RuntimeObservation.RequireCompleteDependencies(combined).Count);
    }

    [Fact]
    public async Task IncompleteAndFailedObservationNeverEnterSuccessfulMemoOrDiskCache() => await TestTemp.Run(async root =>
    {
        string directory = Path.Combine(root, "source"), cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(directory);
        var scene = new JsonObject { ["objects"] = new JsonArray() };
        var project = new JsonObject { ["type"] = "scene", ["file"] = "scene.json" };
        File.WriteAllText(Path.Combine(directory, "scene.json"), scene.ToJsonString());
        File.WriteAllText(Path.Combine(directory, "project.json"), project.ToJsonString());
        using var source = new ProjectSource(directory);
        string hash = await source.SourceHashAsync(TestContext.Current.CancellationToken);
        var memo = new AnalysisMemo();
        int calls = 0;
        var observer = new NativeRuntimeObserver(new("never-run", "never-run", "never-run", []), (_, _) =>
        {
            JsonObject trace = Trace();
            if (++calls == 1) trace["runtime_dependencies_complete"] = false;
            if (calls == 2) trace["status"] = "failed";
            return Task.FromResult(new JsonObject { ["native_result"] = trace,
                ["source_sha256"] = hash, ["source_digest_scope"] = ProjectSource.DigestScope });
        });
        var request = new HybridAnalyzeRequest(1, directory, root, Path.Combine(root, "output"), 4, 4, 30, 1,
            AnalysisCacheDirectory: cache);
        async Task<RuntimeObservation> Observe(AnalysisMemo current)
        {
            Directory.CreateDirectory(request.OutputDirectory);
            return await RuntimeObservation.ObserveAsync(request, source, hash, scene, project, new JsonObject(),
                new SceneGraph(scene), request.OutputDirectory, observer, null, TestContext.Current.CancellationToken, current);
        }
        for (int attempt = 0; attempt < 2; ++attempt)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => Observe(memo));
            Assert.False(Directory.Exists(cache));
            Assert.False(File.Exists(Path.Combine(request.OutputDirectory, "runtime.json")));
        }
        RuntimeObservation success = await Observe(memo);
        Assert.Equal(3, calls);
        string cachedPath = Assert.Single(Directory.GetFiles(cache, "runtime-*.json"));
        var poisoned = JsonNode.Parse(File.ReadAllText(cachedPath))!.AsObject();
        poisoned["runtime_dependencies_complete"] = false;
        File.WriteAllText(cachedPath, poisoned.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => Observe(new AnalysisMemo()));
        Assert.Equal(3, calls); // The invalid disk record is rejected before it can reach grouping.
        Assert.True(success.Trace["runtime_dependencies_complete"]!.GetValue<bool>());
    });
}
