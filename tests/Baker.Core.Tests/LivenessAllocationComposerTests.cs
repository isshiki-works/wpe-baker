using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

// C2.2b：实时判定（Liveness，含唯一的依赖闭包）、分配（Allocation）、构图（Composer）与 SceneGraph 新增的层级/绑定助手。
[Trait("Layer", "L0")]
public class DependencyClosureTests
{
    private static JsonObject Dep(int owner, int target, string operation, string property = "origin", bool initialization = false) => new()
    {
        ["owner"] = owner, ["target"] = target, ["operation"] = operation, ["property"] = property, ["initialization"] = initialization
    };

    // 按对象判：返回每个对象的原因（按记录先后）。
    private static Dictionary<int, List<string>> Close(IEnumerable<int> live, JsonArray dependencies, bool ownerPerRule = true,
        Func<JsonObject, bool>? severedRead = null, Func<JsonObject, bool>? severedWrite = null)
    {
        var reasons = new Dictionary<int, List<string>>();
        var set = new HashSet<int>();
        foreach (int id in live) { set.Add(id); reasons[id] = ["seed"]; }
        bool Mark(int id, string reason)
        {
            if (!reasons.TryGetValue(id, out var list)) reasons[id] = list = [];
            if (!list.Contains(reason)) list.Add(reason);
            return set.Add(id);
        }
        Liveness.Close(dependencies.OfType<JsonObject>(), set.Contains, Mark, ownerPerRule, severedRead ?? (_ => false), severedWrite ?? (_ => false));
        return reasons;
    }

    [Fact]
    public void LiveWriterMakesItsTargetLiveThroughAChain()
    {
        // 2 写 3 排在 1 写 2 前面：第一轮只能到 2，第二轮才到 3。
        var reasons = Close([1], [Dep(2, 3, "write"), Dep(1, 2, "write")]);
        Assert.Equal(new string[] { "written_by_live_controller" }, reasons[2]);
        Assert.Equal(new string[] { "written_by_live_controller" }, reasons[3]);
    }

    [Fact]
    public void OnlyNonInitializationReadsOfLiveTargetsPromoteTheReader()
    {
        var reasons = Close([1], [Dep(2, 1, "read"), Dep(3, 1, "read", initialization: true)]);
        Assert.Equal(new string[] { "reads_live_object" }, reasons[2]);
        Assert.False(reasons.ContainsKey(3));
    }

    [Fact]
    public void LiveReaderPromotesOnlyRuntimeResourceTargets()
    {
        var reasons = Close([1], [Dep(1, 2, "read", "animation"), Dep(1, 3, "read", "origin"), Dep(1, 4, "read", "layerComposite", initialization: true)]);
        Assert.Equal(new string[] { "live_runtime_resource_dependency" }, reasons[2]);
        Assert.False(reasons.ContainsKey(3));
        // 资源读取这条规则不看是否初始化。
        Assert.Equal(new string[] { "live_runtime_resource_dependency" }, reasons[4]);
    }

    [Fact]
    public void SeveredDependenciesAreIgnored()
    {
        var reasons = Close([1], [Dep(1, 2, "write"), Dep(3, 1, "read"), Dep(1, 4, "read", "videoTexture")],
            severedRead: d => d["target"]!.GetValue<int>() == 4, severedWrite: d => d["target"]!.GetValue<int>() == 2);
        Assert.False(reasons.ContainsKey(2));
        Assert.False(reasons.ContainsKey(4));
        Assert.Equal(new string[] { "reads_live_object" }, reasons[3]);
    }

    [Fact]
    public void OwnerPerRuleOnlyChangesTheOrderReasonsAreRecorded()
    {
        // 5 读活目标 1 的动画（把 5 提升），随后 5 又写 1。按对象判时资源规则当场看到 5 已提升；按单元判时要等下一轮。
        JsonArray Deps() => [Dep(5, 1, "read", "animation"), Dep(5, 1, "write")];
        Assert.Equal(new string[] { "seed", "live_runtime_resource_dependency", "written_by_live_controller" }, Close([1], Deps(), ownerPerRule: true)[1]);
        Assert.Equal(new string[] { "seed", "written_by_live_controller", "live_runtime_resource_dependency" }, Close([1], Deps(), ownerPerRule: false)[1]);
    }
}

[Trait("Layer", "L0")]
public class SceneGraphHierarchyTests
{
    [Fact]
    public void WithinFollowsParentsInsideTheScene()
    {
        var graph = new SceneGraph(new JsonObject { ["objects"] = new JsonArray(
            new JsonObject { ["id"] = 1 }, new JsonObject { ["id"] = 2, ["parent"] = 1 }, new JsonObject { ["id"] = 3, ["parent"] = 2 },
            new JsonObject { ["id"] = 4, ["parent"] = 99 }) });
        Assert.True(graph.Within(3, 1));
        Assert.True(graph.Within(2, 2));
        Assert.False(graph.Within(1, 3));
        Assert.False(graph.Within(4, 99));
        Assert.False(graph.Within(42, 42));
    }

    [Fact]
    public void DynamicBindingsAreScriptOrAnimationDriven()
    {
        var graph = new SceneGraph(new JsonObject { ["objects"] = new JsonArray(
            new JsonObject { ["id"] = 1, ["visible"] = new JsonObject { ["script"] = "x" } },
            new JsonObject { ["id"] = 2, ["visible"] = new JsonObject { ["animation"] = new JsonObject() } },
            new JsonObject { ["id"] = 3, ["visible"] = new JsonObject { ["animations"] = new JsonArray() } },
            new JsonObject { ["id"] = 4, ["visible"] = new JsonObject { ["user"] = "show", ["value"] = true } },
            new JsonObject { ["id"] = 5, ["visible"] = false }) });
        Assert.Equal(new bool[] { true, true, true, false, false }, graph.SourceOrder.Select(graph.DynamicVisibility));
        Assert.False(SceneGraph.Animated(new JsonObject { ["script"] = "x" }));
        Assert.True(SceneGraph.Dynamic(new JsonObject { ["script"] = "x" }));
        Assert.True(SceneGraph.Animated(new JsonObject { ["animations"] = new JsonArray() }));
    }
}

// 合成场景走完三个阶段：输入是场景对象、观测到的运行时图层与依赖，断言分配单元、实时集合、省略与分组。
[Trait("Layer", "L1")]
public class AnalysisStagesTests
{
    private sealed record Staged(Liveness Liveness, Allocation Allocation, Composer Composer);

    private static Staged Run(string dir, JsonObject[] objects, JsonArray layers, JsonArray dependencies,
        Func<HybridAnalyzeRequest, HybridAnalyzeRequest>? adjust = null, JsonObject? general = null)
    {
        var scene = new JsonObject { ["general"] = general ?? new JsonObject(), ["objects"] = new JsonArray(objects.Select(o => (JsonNode)o.DeepClone()).ToArray()) };
        File.WriteAllText(Path.Combine(dir, "scene.json"), scene.ToJsonString());
        File.WriteAllText(Path.Combine(dir, "project.json"), new JsonObject { ["type"] = "scene", ["file"] = "scene.json" }.ToJsonString());
        using var source = new ProjectSource(dir);
        var request = new HybridAnalyzeRequest(2, dir, dir, Path.Combine(dir, "analysis"), 64, 64, 60, 1, VideoLayout: "layered");
        request = adjust?.Invoke(request) ?? request;
        var graph = new SceneGraph(scene);
        var observation = new RuntimeObservation(new JsonObject(), dependencies, layers, new JsonObject());
        var properties = new JsonObject();
        var liveness = Liveness.Analyze(request, source, graph, observation, [], parallax: false, daytimeSelector: null, _ => false, _ => false);
        var allocation = Allocation.Plan(graph, observation, liveness, request, properties, parallax: false, _ => false, _ => false);
        var composer = new Composer(request, source, scene, properties, graph, observation, liveness, allocation, parallax: false, [], []);
        return new(liveness, allocation, composer);
    }

    private static JsonObject Image(int id, int? parent = null, JsonNode? visible = null)
    {
        var obj = new JsonObject { ["id"] = id, ["name"] = "layer" + id, ["image"] = "models/a.json" };
        if (parent is int p) obj["parent"] = p;
        if (visible is not null) obj["visible"] = visible;
        return obj;
    }

    private static JsonObject Mesh(int owner, bool hasMesh = true, bool visible = true) => new()
    {
        ["id"] = owner, ["owner"] = owner, ["has_mesh"] = hasMesh, ["visible"] = visible, ["materials"] = new JsonArray()
    };

    private static JsonObject Dep(int owner, int target, string operation, string property = "origin") => new()
    {
        ["owner"] = owner, ["target"] = target, ["operation"] = operation, ["property"] = property, ["initialization"] = false
    };

    [Fact]
    public Task StaticStructuralParentSplitsItsChildrenIntoUnits() => TestTemp.Run(dir =>
    {
        // 10 只有结构字段、观测到无网格：两个孩子各成一个单元；1 是普通根。
        var staged = Run(dir, [Image(1), new JsonObject { ["id"] = 10, ["name"] = "group" }, Image(11, 10), Image(12, 10)],
            [Mesh(1), Mesh(10, hasMesh: false), Mesh(11), Mesh(12)], []);
        Assert.Equal(new[] { 1, 10, 11, 12 }, staged.Allocation.Order);
        Assert.Equal(11, staged.Allocation.UnitOf[11]);
        // 10 自己不画东西，不进组；换了父就要断组，首组承担清屏。
        var groups = staged.Composer.Groups.OfType<JsonObject>().ToArray();
        Assert.Equal("[1]", groups[0]["root_ids"]!.ToJsonString());
        Assert.True(groups[0]["include_scene_clear"]!.GetValue<bool>());
        Assert.Equal("[11,12]", groups[1]["root_ids"]!.ToJsonString());
        Assert.Equal(10, groups[1]["parent_id"]!.GetValue<int>());
        Assert.False(groups[1]["include_scene_clear"]!.GetValue<bool>());
        return Task.CompletedTask;
    });

    [Fact]
    public Task ScriptedStructuralParentKeepsItsSubtreeTogether() => TestTemp.Run(dir =>
    {
        var parent = new JsonObject { ["id"] = 10, ["name"] = "group", ["origin"] = new JsonObject { ["script"] = "export function update(v){return v;}", ["value"] = "0 0 0" } };
        var staged = Run(dir, [parent, Image(11, 10)], [Mesh(10, hasMesh: false), Mesh(11)], []);
        Assert.Equal(new int[] { 10 }, staged.Allocation.Order);
        Assert.Equal(10, staged.Allocation.UnitOf[11]);
        return Task.CompletedTask;
    });

    [Fact]
    public Task UnitClosurePromotesWholeUnitsAndMarksSharedHierarchy() => TestTemp.Run(dir =>
    {
        // 2 的脚本读指针 → 实时；它写 3（另一根）→ 3 所在单元整个实时，3 的孩子 4 只是"同单元连带"。
        // 4 本身不实时，按对象判的闭包不会动 5；按单元判时 4 所在单元已实时，它写 5 就把 5 连坐。
        var pointer = Image(2);
        pointer["origin"] = new JsonObject { ["script"] = "export function update(v){ return input.cursorWorldPosition; }", ["value"] = "0 0 0" };
        var staged = Run(dir, [Image(1), pointer, Image(3), Image(4, 3), Image(5)], [Mesh(1), Mesh(2), Mesh(3), Mesh(4), Mesh(5)],
            [Dep(2, 3, "write"), Dep(4, 5, "write")]);
        Assert.Contains("pointer_api", staged.Liveness.Reasons[2]);
        Assert.Equal(new string[] { "written_by_live_controller" }, staged.Liveness.Reasons[3]);
        Assert.Equal(new string[] { "shares_live_hierarchy" }, staged.Liveness.Reasons[4]);
        Assert.Equal(new string[] { "written_by_live_controller" }, staged.Liveness.Reasons[5]);
        Assert.Equal(new int[] { 2, 3, 4, 5 }, staged.Allocation.LiveIds.Order());
        Assert.Equal("[{\"video_group\":\"group-1\"},{\"live_root\":2},{\"live_root\":3},{\"live_root\":5}]", staged.Composer.Composition.ToJsonString());
        return Task.CompletedTask;
    });

    [Fact]
    public Task FixedHiddenSubtreeIsOmittedButDynamicVisibilityIsNot() => TestTemp.Run(dir =>
    {
        // 4 的快照值同样是隐藏（属性没给，取绑定默认值 false），但 visible 绑了脚本：不省略，作为隐藏脚本宿主留实时。
        // 5 的 visible 由动画驱动、观测时隐藏：运行时可能显示，照样进视频组。
        var scripted = new JsonObject { ["user"] = "show", ["value"] = false, ["script"] = "export function update(v){ return v; }" };
        var animated = new JsonObject { ["animation"] = new JsonObject(), ["value"] = false };
        var staged = Run(dir, [Image(1), Image(2, visible: false), Image(3, 2), Image(4, visible: scripted), Image(5, visible: animated)],
            [Mesh(1), Mesh(2), Mesh(3), Mesh(4), Mesh(5, visible: false)], []);
        Assert.Equal(new int[] { 2, 3 }, staged.Allocation.OmittedIds.Order());
        Assert.False(staged.Composer.Draws(2));
        Assert.False(staged.Composer.Visible(3));
        Assert.True(staged.Composer.Draws(4));
        Assert.False(staged.Composer.Visible(4));
        Assert.Equal(new string[] { "hidden_script_controller" }, staged.Liveness.Reasons[4]);
        Assert.False(staged.Composer.Visible(5));
        Assert.Equal("[{\"video_group\":\"group-1\"},{\"live_root\":4},{\"video_group\":\"group-2\"}]", staged.Composer.Composition.ToJsonString());
        Assert.Equal("[5]", staged.Composer.Groups[1]!["root_ids"]!.ToJsonString());
        return Task.CompletedTask;
    });

    [Fact]
    public Task ExcludedLayersAreOmittedWithTheirSubtreeAndUnknownIdsAreRejected() => TestTemp.Run(dir =>
    {
        JsonObject[] objects = [Image(1), Image(2), Image(3, 2)];
        JsonArray layers = [Mesh(1), Mesh(2), Mesh(3)];
        var staged = Run(dir, objects, layers, [], request => request with { ExcludedLayerIds = [2, 2] });
        Assert.Equal(new int[] { 2 }, staged.Allocation.ExcludedRoots);
        Assert.Equal(new int[] { 2, 3 }, staged.Allocation.ExcludedIds.Order());
        Assert.Equal(new int[] { 2, 3 }, staged.Allocation.OmittedIds.Order());
        Assert.Throws<InvalidDataException>(() => Run(dir, objects, layers.DeepClone().AsArray(), [], request => request with { ExcludedLayerIds = [7] }));
        return Task.CompletedTask;
    });

    [Fact]
    public Task LiveRootBetweenVideoRootsSplitsGroupsAndForegroundMovesTextOverlays() => TestTemp.Run(dir =>
    {
        // 2 是读指针的文字（独立覆盖层）：默认把 1 与 3 分成两组；前景置顶时 2 挪到最后，1、3 合成一组。
        var text = new JsonObject { ["id"] = 2, ["name"] = "clock", ["text"] = new JsonObject { ["value"] = "x" },
            ["origin"] = new JsonObject { ["script"] = "export function update(v){ return input.cursorWorldPosition; }", ["value"] = "0 0 0" } };
        JsonObject[] objects = [Image(1), text, Image(3)];
        var preserve = Run(dir, objects, [Mesh(1), Mesh(2), Mesh(3)], []);
        Assert.Equal(2, preserve.Composer.Groups.Count);
        Assert.Equal("available", preserve.Composer.OcclusionTradeoff["status"]!.GetValue<string>());
        var foreground = Run(dir, objects, [Mesh(1), Mesh(2), Mesh(3)], [], request => request with { LiveOverlayPlacement = "foreground" });
        Assert.Equal(new int[] { 1, 3, 2 }, foreground.Composer.RootOrder);
        var group = Assert.Single(foreground.Composer.Groups.OfType<JsonObject>());
        Assert.Equal("[1,3]", group["root_ids"]!.ToJsonString());
        Assert.Equal("applied", foreground.Composer.OcclusionTradeoff["status"]!.GetValue<string>());
        return Task.CompletedTask;
    });

    [Fact]
    public Task RandomParticleSuffixIsOptionalForeground() => TestTemp.Run(dir =>
    {
        var random = Image(2);
        random["alpha"] = new JsonObject { ["script"] = "export function update(v){ return Math.random(); }", ["value"] = 1 };
        var staged = Run(dir, [Image(1), random], [Mesh(1), Mesh(2)], []);
        Assert.Equal(new int[] { 2 }, staged.Composer.OptionalForeground);
        // 首组首根要承担清屏，不能剥；首根之后紧跟的非随机根挡住后缀，同样不剥。
        Assert.Empty(Run(dir, [random], [Mesh(2)], []).Composer.OptionalForeground);
        Assert.Empty(Run(dir, [random, Image(1)], [Mesh(2), Mesh(1)], []).Composer.OptionalForeground);
        return Task.CompletedTask;
    });
}
