using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

// F17: CPU-only tests exercise the real observer projection and memo/disk commit path.
// The Raw delegate supplies receipts; these tests do not execute WPE or source JavaScript.
[Trait("Layer", "L1")]
public class RuntimeObservationSourceIdentityTests
{
    [Fact]
    public async Task TemporaryCapturePathDoesNotLeakIntoReusableRuntimeTrace() => await TestTemp.Run(async root =>
    {
        string directory = Path.Combine(root, "source");
        JsonObject scene = Scene();
        WriteSource(directory, scene);
        using var source = new ProjectSource(directory);
        string hash = await source.SourceHashAsync(TestContext.Current.CancellationToken);
        string? captured = null;
        int calls = 0;
        var observer = new NativeRuntimeObserver(NoProcesses, (capture, _) =>
        {
            ++calls; captured = capture.Source;
            JsonObject result = Raw(hash);
            result["native_result"]!["source"] = capture.Source;
            return Task.FromResult(result);
        });
        var request = new HybridAnalyzeRequest(1, directory, root, Path.Combine(root, "first"), 4, 4, 30, 1);
        RuntimeObservation first = await Observe(request, source, hash, scene, observer, new AnalysisMemo());
        Assert.Equal(source.SourcePath, first.Trace["source"]!.GetValue<string>());
        Assert.NotNull(captured);
        Assert.NotEqual(source.SourcePath, captured);
        Assert.False(Directory.Exists(Path.GetDirectoryName(captured)!));
        RuntimeObservation replay = await Observe(request with { OutputDirectory = Path.Combine(root, "replay"),
            RuntimeTraceFile = Path.Combine(request.OutputDirectory, "runtime.json") }, source, hash, scene, observer, new AnalysisMemo());
        Assert.Equal(source.SourcePath, replay.Trace["source"]!.GetValue<string>());
        Assert.Equal(1, calls);
    });

    [Fact]
    public async Task PackageObservationKeepsPhysicalRepresentationAndSelectedEntry() => await TestTemp.Run(async root =>
    {
        string directory = Path.Combine(root, "source");
        JsonObject scene = Scene();
        WriteSource(directory, scene);
        byte[] payload = System.Text.Encoding.UTF8.GetBytes(scene.ToJsonString());
        using (var writer = new BinaryWriter(File.Create(Path.Combine(directory, "scene.pkg"))))
        {
            void Text(string value) { byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
            Text("PKGV0017"); writer.Write(1); Text("scene.json"); writer.Write(0); writer.Write(payload.Length); writer.Write(payload);
        }
        using var source = new ProjectSource(directory);
        string hash = await source.SourceHashAsync(TestContext.Current.CancellationToken);
        var observer = new NativeRuntimeObserver(NoProcesses, async (capture, token) =>
        {
            Assert.Equal(".pkg", Path.GetExtension(capture.Source));
            Assert.NotEqual(source.SourcePath, capture.Source);
            Assert.Equal(new DirectoryInfo(source.DirectoryPath).Name, new DirectoryInfo(Path.GetDirectoryName(capture.Source)!).Name);
            using var snapshot = new ProjectSource(capture.Source);
            Assert.Equal(hash, await snapshot.SourceHashAsync(token));
            return Raw(hash);
        });
        var request = new HybridAnalyzeRequest(1, directory, root, Path.Combine(root, "output"), 4, 4, 30, 1);
        await Observe(request, source, hash, scene, observer, new AnalysisMemo());
        Assert.Empty(Directory.GetDirectories(request.OutputDirectory, ".runtime-source-*"));
        Assert.True(File.Exists(source.SourcePath));
    });

    [Fact]
    public async Task CopyWindowAbaCannotBindChangedPhysicalResourceToOriginalKey() => await TestTemp.Run(async root =>
    {
        string directory = Path.Combine(root, "source"), cache = Path.Combine(root, "cache");
        JsonObject scene = Scene();
        scene["objects"]![0]!["alpha"] = new JsonObject { ["value"] = 1,
            ["script"] = "'use strict'; export function update(value) { return WEMath.smoothStep(0.499999, 0.5, engine.timeOfDay); }" };
        WriteSource(directory, scene);
        string resource = Path.Combine(directory, "resource.bin");
        File.WriteAllBytes(resource, [1, 2, 3, 4]);
        DateTime timestamp = File.GetLastWriteTimeUtc(resource);
        using var source = new ProjectSource(directory);
        string hash = await source.SourceHashAsync(TestContext.Current.CancellationToken);
        int calls = 0;
        bool copiedB = false, restoredA = false;
        var observer = new NativeRuntimeObserver(NoProcesses, (_, _) => { ++calls; return Task.FromResult(Raw(hash)); }, name =>
        {
            if (name == "resource.bin") { File.WriteAllBytes(resource, [4, 3, 2, 1]); copiedB = true; }
            if (name == "scene.json") { File.WriteAllBytes(resource, [1, 2, 3, 4]); restoredA = true; }
            File.SetLastWriteTimeUtc(resource, timestamp);
        });
        var request = new HybridAnalyzeRequest(1, directory, root, Path.Combine(root, "output"), 4, 4, 30, 1,
            DaytimeSplit: true, DaytimeState: "12-24", AnalysisCacheDirectory: cache);
        var error = await Assert.ThrowsAsync<IOException>(() => Observe(request, source, hash, scene, observer, new AnalysisMemo()));
        Assert.Contains("snapshot does not match", error.Message);
        Assert.True(copiedB && restoredA);
        Assert.Equal(hash, await source.SourceHashAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, calls);
        Assert.False(Directory.Exists(cache));
        Assert.Empty(Directory.GetDirectories(request.OutputDirectory, ".runtime-source-*"));
    });

    [Fact]
    public async Task SameLengthResourceWithRestoredTimestampCannotEnterDerivedCache() => await TestTemp.Run(async root =>
    {
        string directory = Path.Combine(root, "source"), cache = Path.Combine(root, "cache");
        JsonObject scene = Scene();
        scene["objects"]![0]!["alpha"] = new JsonObject { ["value"] = 1,
            ["script"] = "'use strict'; export function update(value) { return WEMath.smoothStep(0.499999, 0.5, engine.timeOfDay); }" };
        WriteSource(directory, scene);
        string resource = Path.Combine(directory, "resource.bin");
        File.WriteAllBytes(resource, [1, 2, 3, 4]);
        DateTime originalTime = File.GetLastWriteTimeUtc(resource);
        using var source = new ProjectSource(directory);
        string hash = await source.SourceHashAsync(TestContext.Current.CancellationToken, reuse: true);
        File.WriteAllBytes(resource, [4, 3, 2, 1]);
        File.SetLastWriteTimeUtc(resource, originalTime);
        int calls = 0;
        var observer = new NativeRuntimeObserver(NoProcesses, (_, _) => { ++calls; return Task.FromResult(Raw(hash)); });
        var request = new HybridAnalyzeRequest(1, directory, root, Path.Combine(root, "output"), 4, 4, 30, 1,
            DaytimeSplit: true, DaytimeState: "12-24", AnalysisCacheDirectory: cache);
        await Assert.ThrowsAsync<IOException>(() => Observe(request, source, hash, scene, observer, new AnalysisMemo()));
        Assert.Equal(0, calls);
        Assert.False(Directory.Exists(cache));
    });

    private static readonly NativeTools NoProcesses = new("never-run", "never-run", "never-run", []);

    private static JsonObject Project() => new() { ["type"] = "scene", ["file"] = "scene.json" };
    private static JsonObject Scene() => new() { ["objects"] = new JsonArray(new JsonObject {
        ["id"] = 1, ["name"] = "A", ["visible"] = true }) };
    private static void WriteSource(string directory, JsonObject scene)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "project.json"), Project().ToJsonString());
        File.WriteAllText(Path.Combine(directory, "scene.json"), scene.ToJsonString());
    }
    private static JsonObject Raw(string hash, JsonObject? trace = null) => new() {
        ["status"] = "completed", ["source_sha256"] = hash, ["source_digest_scope"] = ProjectSource.DigestScope,
        ["native_result"] = trace ?? new JsonObject { ["status"] = "complete", ["runtime_dependencies"] = new JsonArray(),
            ["runtime_layers"] = new JsonArray(), ["source_script_error_count"] = 0, ["source_script_errors"] = new JsonArray() } };
    private static async Task<RuntimeObservation> Observe(HybridAnalyzeRequest request, ProjectSource source, string hash,
        JsonObject scene, NativeRuntimeObserver observer, AnalysisMemo memo)
    {
        Directory.CreateDirectory(request.OutputDirectory);
        return await RuntimeObservation.ObserveAsync(request, source, hash, scene, Project(), new JsonObject(), new SceneGraph(scene),
            request.OutputDirectory, observer, null, TestContext.Current.CancellationToken, memo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task F17_MismatchedRawIdentityNeverEntersMemoOrDiskEvenAfterAba(bool restoreBeforeReceipt) =>
        await TestTemp.Run(async root =>
        {
            string directory = Path.Combine(root, "source"), cache = Path.Combine(root, "cache");
            JsonObject scene = Scene();
            WriteSource(directory, scene);
            using var source = new ProjectSource(directory);
            string hashA = await source.SourceHashAsync(TestContext.Current.CancellationToken);
            var memo = new AnalysisMemo();
            var request = new HybridAnalyzeRequest(1, directory, root, Path.Combine(root, "first"), 4, 4, 30, 1,
                AnalysisCacheDirectory: cache);
            int calls = 0;
            JsonObject? wrongRaw = null;
            var observer = new NativeRuntimeObserver(NoProcesses, async (capture, token) =>
            {
                ++calls;
                if (calls != 1) return Raw(hashA);
                var changed = scene.DeepClone().AsObject();
                changed["objects"]![0]!["name"] = "B";
                string captureScene = Path.Combine(Path.GetDirectoryName(capture.Source)!, "scene.json");
                File.WriteAllText(captureScene, changed.ToJsonString());
                using var actual = new ProjectSource(capture.Source);
                string hashB = await actual.SourceHashAsync(token);
                Assert.NotEqual(hashA, hashB);
                wrongRaw = Raw(hashB);
                if (restoreBeforeReceipt) File.WriteAllText(captureScene, scene.ToJsonString());
                return wrongRaw;
            });
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Observe(request, source, hashA, scene, observer, memo));
            Assert.Contains("expected source identity", error.Message);
            Assert.False(Directory.Exists(cache));
            Assert.False(File.Exists(Path.Combine(request.OutputDirectory, "runtime.json")));
            Assert.Equal("completed", wrongRaw!["status"]!.GetValue<string>());
            Assert.NotEqual(hashA, wrongRaw["source_sha256"]!.GetValue<string>());
            Assert.Null(wrongRaw["native_result"]!["observation_identity"]);

            File.WriteAllText(Path.Combine(directory, "scene.json"), scene.ToJsonString());
            request = request with { OutputDirectory = Path.Combine(root, "retry") };
            RuntimeObservation retry = await Observe(request, source, hashA, scene, observer, memo);
            Assert.Equal(2, calls); // A failed observation was evicted from this same memo.
            Assert.Equal(hashA, retry.Trace["observation_identity"]!["source_sha256"]!.GetValue<string>());
            Assert.Single(Directory.GetFiles(cache, "runtime-*.json"));
            request = request with { OutputDirectory = Path.Combine(root, "disk-hit") };
            RuntimeObservation diskHit = await Observe(request, source, hashA, scene, observer, new AnalysisMemo());
            Assert.Equal(2, calls);
            Assert.Equal(0, diskHit.ScriptFaults(false).Count);
        });

    [Theory]
    [InlineData("source_sha256")]
    [InlineData("source_digest_scope")]
    public async Task SceneDriftAndMissingRawCredentialsAreRejectedWithoutCaching(string missingField) => await TestTemp.Run(async root =>
    {
        string directory = Path.Combine(root, "source"), cache = Path.Combine(root, "cache");
        JsonObject scene = Scene();
        WriteSource(directory, scene);
        using var source = new ProjectSource(directory);
        string hash = await source.SourceHashAsync(TestContext.Current.CancellationToken);
        var request = new HybridAnalyzeRequest(1, directory, root, Path.Combine(root, "drift"), 4, 4, 30, 1,
            AnalysisCacheDirectory: cache);
        int calls = 0;
        var observer = new NativeRuntimeObserver(NoProcesses, (_, _) =>
        {
            ++calls;
            JsonObject raw = Raw(hash);
            raw.Remove(missingField);
            return Task.FromResult(raw);
        });
        File.WriteAllText(Path.Combine(directory, "scene.json"), """{"objects":[]}""");
        await Assert.ThrowsAsync<IOException>(() => Observe(request, source, hash, scene, observer, new AnalysisMemo()));
        Assert.Equal(0, calls);
        WriteSource(directory, scene);
        request = request with { OutputDirectory = Path.Combine(root, "missing") };
        await Assert.ThrowsAsync<InvalidDataException>(() => Observe(request, source, hash, scene, observer, new AnalysisMemo()));
        Assert.Equal(1, calls);
        Assert.False(Directory.Exists(cache));
    });

    [Fact]
    public async Task RawFailureKeepsItsOriginalExceptionAndNeverCaches() => await TestTemp.Run(async root =>
    {
        string directory = Path.Combine(root, "source");
        JsonObject scene = Scene();
        WriteSource(directory, scene);
        using var source = new ProjectSource(directory);
        string hash = await source.SourceHashAsync(TestContext.Current.CancellationToken);
        var failure = new IOException("original Raw failure");
        var observer = new NativeRuntimeObserver(NoProcesses, (_, _) => Task.FromException<JsonObject>(failure));
        var request = new HybridAnalyzeRequest(1, directory, root, Path.Combine(root, "output"), 4, 4, 30, 1,
            AnalysisCacheDirectory: Path.Combine(root, "cache"));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => Observe(request, source, hash, scene, observer, new AnalysisMemo())));
        Assert.False(Directory.Exists(request.AnalysisCacheDirectory));
    });

    [Fact]
    public async Task OldRuntimeCacheWithoutCaptureIdentityIsNotReusedOrDeleted() => await TestTemp.Run(async root =>
    {
        string directory = Path.Combine(root, "source"), cache = Path.Combine(root, "cache");
        JsonObject scene = Scene();
        WriteSource(directory, scene);
        using var source = new ProjectSource(directory);
        string hash = await source.SourceHashAsync(TestContext.Current.CancellationToken);
        var key = new ObservationKey(hash, scene, new JsonObject(), root, 4, 4, 30, 1, false, null, "missing");
        string oldKey = "runtime-" + AnalysisCache.Key(key);
        var legacy = new JsonObject { ["sentinel"] = "legacy runtime cache" };
        AnalysisCache.Write(cache, oldKey, legacy);
        int calls = 0;
        var observer = new NativeRuntimeObserver(NoProcesses, (_, _) => { ++calls; return Task.FromResult(Raw(hash)); });
        var request = new HybridAnalyzeRequest(1, directory, root, Path.Combine(root, "output"), 4, 4, 30, 1,
            AnalysisCacheDirectory: cache);
        await Observe(request, source, hash, scene, observer, new AnalysisMemo());
        Assert.Equal(1, calls);
        Assert.Equal(legacy.ToJsonString(), File.ReadAllText(Path.Combine(cache, oldKey + ".json")));
        Assert.NotNull(AnalysisCache.Read(cache, "runtime-" + key.Hash()));
    });

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LegalDaytimeAndAudioCopiesBindTheirOwnCaptureDigest(bool daytime, bool audio) => await TestTemp.Run(async root =>
    {
        string directory = Path.Combine(root, "source"), cache = Path.Combine(root, "cache");
        JsonObject scene = Scene();
        if (daytime) scene["objects"]![0]!["alpha"] = new JsonObject { ["value"] = 1,
            ["script"] = "'use strict'; export function update(value) { return WEMath.smoothStep(0.499999, 0.5, engine.timeOfDay); }" };
        if (audio) scene["objects"]![0]!["effects"] = new JsonArray(new JsonObject {
            ["id"] = 10, ["file"] = "effects/audio.json", ["passes"] = new JsonArray(new JsonObject()) });
        WriteSource(directory, scene);
        if (audio)
        {
            Directory.CreateDirectory(Path.Combine(directory, "effects"));
            Directory.CreateDirectory(Path.Combine(directory, "materials"));
            File.WriteAllText(Path.Combine(directory, "effects/audio.json"), """{"passes":[{"material":"materials/audio.json"}]}""");
            File.WriteAllText(Path.Combine(directory, "materials/audio.json"), """{"passes":[{"shader":"effects/audio"}]}""");
        }
        JsonObject originalScene = scene.DeepClone().AsObject();
        using var source = new ProjectSource(directory);
        string originalHash = await source.SourceHashAsync(TestContext.Current.CancellationToken);
        var capturedHashes = new List<string>();
        var observer = new NativeRuntimeObserver(NoProcesses, async (capture, token) =>
        {
            using var actual = new ProjectSource(capture.Source);
            string hash = await actual.SourceHashAsync(token);
            capturedHashes.Add(hash);
            JsonObject capturedScene = actual.ReadJson(actual.SceneResource);
            if (daytime) Assert.Null(capturedScene["objects"]![0]!["alpha"]!["script"]);
            JsonObject trace = Raw(hash)["native_result"]!.DeepClone().AsObject();
            if (capturedScene["objects"]![0]!["effects"] is JsonArray { Count: > 0 })
                trace["runtime_layers"] = new JsonArray(new JsonObject { ["owner"] = 1, ["materials"] = new JsonArray(new JsonObject {
                    ["role"] = "effect", ["shader"] = "effects/audio", ["uses_audio_spectrum"] = true,
                    ["active_uniforms"] = new JsonArray(), ["textures"] = new JsonArray() }) });
            return Raw(hash, trace);
        });
        var request = new HybridAnalyzeRequest(1, directory, root, Path.Combine(root, "output"), 4, 4, 30, 1,
            AudioEffects: audio ? "omit" : "preserve", DaytimeSplit: daytime, DaytimeState: daytime ? "12-24" : null,
            AnalysisCacheDirectory: cache);
        if (daytime) Assert.NotNull(DaytimeSplit.PrepareVideoObservation(scene, new JsonObject(), "12-24"));
        RuntimeObservation result = await Observe(request, source, originalHash, scene, observer, new AnalysisMemo());
        Assert.Equal(audio ? 2 : 1, capturedHashes.Count);
        Assert.NotEqual(originalHash, capturedHashes[^1]);
        Assert.Equal(capturedHashes[^1], result.Trace["observation_identity"]!["source_sha256"]!.GetValue<string>());
        Assert.Equal(originalHash, await source.SourceHashAsync(TestContext.Current.CancellationToken));
        if (audio) Assert.Equal("applied", result.AudioEffectChoice["status"]!.GetValue<string>());
        int count = capturedHashes.Count;
        request = request with { OutputDirectory = Path.Combine(root, "disk-hit") };
        await Observe(request, source, originalHash, originalScene, observer, new AnalysisMemo());
        Assert.Equal(count, capturedHashes.Count);
        Assert.False(Directory.Exists(Path.Combine(request.OutputDirectory, "audio-choice-source")));
        Assert.False(Directory.Exists(Path.Combine(request.OutputDirectory, "runtime-probe-daytime-source")));
    });
}
