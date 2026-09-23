using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

// C2.2a：场景层级（SceneGraph）与运行时观测阶段（RuntimeObservation / IRuntimeObserver / ObservationKey）。
[Trait("Layer", "L0")]
public class SceneGraphTests
{
    private static JsonObject Scene(params JsonObject[] objects) => new() { ["objects"] = new JsonArray(objects) };
    private static JsonObject Obj(int id, int? parent = null) => parent is int p ? new() { ["id"] = id, ["parent"] = p } : new() { ["id"] = id };

    [Fact]
    public void RootFollowsParentsInsideTheSceneOnly()
    {
        // 4 的父不在场景里，按根处理；作者顺序不是 id 顺序。
        var graph = new SceneGraph(Scene(Obj(5), Obj(3, 2), Obj(1), Obj(2, 1), Obj(4, 99)));
        Assert.Equal(new[] { 5, 3, 1, 2, 4 }, graph.SourceOrder);
        Assert.Equal(new[] { 5, 3, 1, 2, 4 }, graph.Objects.Keys);
        Assert.Equal(1, graph.RootOf[3]);
        Assert.Equal(1, graph.RootOf[2]);
        Assert.Equal(4, graph.RootOf[4]);
        Assert.Equal(new[] { 5, 1, 4 }, graph.Roots);
        Assert.Equal(2, graph.Parent(3));
        Assert.Null(graph.Parent(4));
    }

    [Fact]
    public void ParentCycleIsRejected() =>
        Assert.Throws<InvalidDataException>(() => new SceneGraph(Scene(Obj(1, 2), Obj(2, 1))));
}

[Trait("Layer", "L0")]
public class RuntimeObservationLinkTests
{
    private static JsonObject Dependency(int owner, int target, bool initialization) => new()
    {
        ["owner"] = owner, ["target"] = target, ["operation"] = "read", ["property"] = "layerComposite", ["initialization"] = initialization
    };

    private static JsonArray Layers(params (int Owner, string[] Textures)[] layers) => new(layers.Select(layer => (JsonNode)new JsonObject
    {
        ["owner"] = layer.Owner,
        ["materials"] = new JsonArray(new JsonObject { ["textures"] = new JsonArray(layer.Textures.Select(t => (JsonNode)t).ToArray()) })
    }).ToArray());

    private static JsonArray Link(JsonArray dependencies, JsonArray layers)
    {
        var graph = new SceneGraph(new JsonObject { ["objects"] = new JsonArray(
            new JsonObject { ["id"] = 1 }, new JsonObject { ["id"] = 2 }, new JsonObject { ["id"] = 3 }) });
        RuntimeObservation.LinkComposites(graph, dependencies, layers);
        return dependencies;
    }

    [Fact]
    public void ObservedProducerBecomesALayerCompositeRead()
    {
        var added = Link([], Layers((1, ["_rt_link_2", "materials/x.tex"]), (2, [])));
        var edge = Assert.Single(added.OfType<JsonObject>());
        Assert.Equal(1, edge["owner"]!.GetValue<int>());
        Assert.Equal(2, edge["target"]!.GetValue<int>());
        Assert.False(edge["initialization"]!.GetValue<bool>());
    }

    [Fact]
    public void UnobservedProducersSelfLinksAndExistingReadsAddNothing()
    {
        // 3 没有运行时图层（不是生产者）；自链接不算；已有非初始化读不重复加。
        Assert.Empty(Link([], Layers((1, ["_rt_link_3", "_rt_link_1"]), (2, []))));
        Assert.Single(Link([Dependency(1, 2, initialization: false)], Layers((1, ["_rt_link_2"]), (2, []))));
    }

    [Fact]
    public void InitializationReadDoesNotSuppressTheLink() =>
        Assert.Equal(2, Link([Dependency(1, 2, initialization: true)], Layers((1, ["_rt_link_2"]), (2, []))).Count);
}

[Trait("Layer", "L0")]
public class ScriptFaultEvidenceTests
{
    private static RuntimeObservation With(JsonObject trace) => new(trace, [], [], new JsonObject());

    [Fact]
    public void OfflineTraceMayOmitEvidenceButALiveObservationMayNot()
    {
        var (available, count, errors) = With(new JsonObject()).ScriptFaults(offlineTrace: true);
        Assert.False(available);
        Assert.Null(count);
        Assert.Empty(errors);
        Assert.Throws<InvalidDataException>(() => With(new JsonObject()).ScriptFaults(offlineTrace: false));
    }

    [Fact]
    public void EvidenceMustBeComplete()
    {
        var trace = new JsonObject { ["source_script_error_count"] = 1, ["source_script_errors"] = new JsonArray(new JsonObject { ["owner"] = 3 }) };
        var (available, count, errors) = With(trace).ScriptFaults(offlineTrace: false);
        Assert.True(available);
        Assert.Equal(1, count);
        Assert.Same(trace["source_script_errors"], errors);
        Assert.Throws<InvalidDataException>(() => With(new JsonObject {
            ["source_script_error_count"] = 2, ["source_script_errors"] = new JsonArray(new JsonObject()) }).ScriptFaults(offlineTrace: true));
        Assert.Throws<InvalidDataException>(() => With(new JsonObject {
            ["source_script_error_count"] = 1, ["source_script_errors"] = new JsonArray(1) }).ScriptFaults(offlineTrace: true));
    }
}

[Trait("Layer", "L1")]
public class RuntimeObservationStageTests
{
    /// <summary>假观测器：记下每次请求，返回一份完整 trace，或按要求抛错。</summary>
    private sealed class FakeObserver(string identity = "fake", Exception? failure = null) : IRuntimeObserver
    {
        public List<ObservationRequest> Requests { get; } = [];
        public string Identity => identity;
        public Task<JsonObject> ObserveAsync(ObservationRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (failure is not null) throw failure;
            return Task.FromResult(new JsonObject
            {
                ["status"] = "complete", ["runtime_dependencies"] = new JsonArray(), ["runtime_layers"] = new JsonArray(),
                ["source_script_error_count"] = 0, ["source_script_errors"] = new JsonArray()
            });
        }
    }

    private static async Task<RuntimeObservation> Observe(string root, string name, FakeObserver observer, string? device, string? interaction)
    {
        string sourceDirectory = Path.Combine(root, "source");
        if (!Directory.Exists(sourceDirectory))
        {
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllText(Path.Combine(sourceDirectory, "project.json"), "{\"type\":\"scene\",\"file\":\"scene.json\"}");
            File.WriteAllText(Path.Combine(sourceDirectory, "scene.json"), "{\"objects\":[{\"id\":1}]}");
        }
        using var source = new ProjectSource(sourceDirectory);
        var scene = source.ReadJson(source.SceneResource);
        string output = Path.Combine(root, name);
        Directory.CreateDirectory(output);
        var request = new HybridAnalyzeRequest(2, sourceDirectory, "assets", output, 1920, 1080, DeviceUuid: device,
            Interaction: interaction, AnalysisCacheDirectory: Path.Combine(root, "cache"));
        return await RuntimeObservation.ObserveAsync(request, source, "hash", scene, new JsonObject(), new JsonObject(),
            new SceneGraph(scene), output, observer, null, CancellationToken.None);
    }

    [Fact]
    public async Task CacheKeyIgnoresOutputAndDeviceWithoutGpuTiming() => await TestTemp.Run(async root =>
    {
        var observer = new FakeObserver();
        await Observe(root, "first", observer, device: "a", interaction: null);
        var second = await Observe(root, "second", observer, device: "b", interaction: null);
        var request = Assert.Single(observer.Requests);
        Assert.Equal((512u, 288u), (request.Key.Width, request.Key.Height));
        Assert.Equal("complete", second.Trace["status"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(root, "second", "runtime.json")));
    });

    [Fact]
    public async Task GpuTimingKeysOnDeviceAndRendererIdentity() => await TestTemp.Run(async root =>
    {
        var observer = new FakeObserver();
        await Observe(root, "first", observer, device: "a", interaction: "fixed");
        await Observe(root, "second", observer, device: "b", interaction: "fixed");
        await Observe(root, "third", new FakeObserver("other-renderer"), device: "b", interaction: "fixed");
        Assert.Equal(2, observer.Requests.Count);
        Assert.True(observer.Requests[0].Key.GpuTiming);
        Assert.Equal("b", observer.Requests[1].DeviceUuid);
        Assert.Equal(3, Directory.GetFiles(Path.Combine(root, "cache"), "runtime-*.json").Length);
    });

    [Fact]
    public async Task UnreadableAssetBecomesAToolLimitation() => await TestTemp.Run(async root =>
    {
        var observer = new FakeObserver(failure: new IOException("MDL parse failed: models/a.mdl: unsupported header"));
        await Assert.ThrowsAsync<AnalysisToolLimitationException>(() => Observe(root, "first", observer, null, null));
        var plain = new FakeObserver(failure: new IOException("disk on fire"));
        await Assert.ThrowsAsync<IOException>(() => Observe(root, "second", plain, null, null));
    });
}

[Trait("Layer", "L1")]
public class NativeRuntimeObserverIdentityTests
{
    [Fact]
    public async Task IdentityFollowsRendererContentNotTimestamp() => await TestTemp.Run(root =>
    {
        string renderer = Path.Combine(root, "wpe-render.exe");
        IRuntimeObserver Observer() => new NativeRuntimeObserver(new NativeTools(renderer, "ffmpeg", "ffprobe", []));
        Assert.Equal("missing", Observer().Identity);
        File.WriteAllText(renderer, "build-1");
        string first = Observer().Identity;
        File.SetLastWriteTimeUtc(renderer, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(first, Observer().Identity);
        File.WriteAllText(renderer, "build-2");
        File.SetLastWriteTimeUtc(renderer, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.NotEqual(first, Observer().Identity);
        Assert.StartsWith("sha256:", first);
    });
}
