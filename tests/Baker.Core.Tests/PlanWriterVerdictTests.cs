using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

// C2.2d2：拒因单态（PlanBlockers 两个字段一起写）、PlanWriter 的来源记录与字段位置、SceneGraph 的属性解析。
[Trait("Layer", "L0")]
public class PlanBlockersSingleStateTests
{
    private static readonly Blocker Shell = new(BlockerCode.VideoShell);
    private static readonly Blocker Perspective = new(BlockerCode.PerspectiveNeedsScreenspace);

    [Fact]
    public void AddWritesTextAndLocalizedTogetherAndSkipsDuplicates()
    {
        var plan = new JsonObject { ["blockers"] = new JsonArray() };
        PlanBlockers.Add(plan, Shell);
        PlanBlockers.Add(plan, Perspective);
        PlanBlockers.Add(plan, Shell);
        Assert.Equal([Shell.Text, Perspective.Text], plan["blockers"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Equal([BlockerCode.VideoShell, BlockerCode.PerspectiveNeedsScreenspace], PlanBlockers.Codes(plan));
        Assert.True(JsonNode.DeepEquals(plan["blockers_localized"]![1], Perspective.Localized()));
    }

    [Fact]
    public void SetRendersEveryBlockerEvenRepeated()
    {
        // 初判拒因可以重复（多台相机各记一条），渲染时不去重。
        var plan = new JsonObject();
        PlanBlockers.Set(plan, [Shell, Shell]);
        Assert.Equal(2, plan["blockers"]!.AsArray().Count);
        Assert.Equal(2, plan["blockers_localized"]!.AsArray().Count);
    }

    [Fact]
    public void WholeLayerRefreshCopiesTheLocalizedArrayToo()
    {
        var plan = new JsonObject { ["blockers"] = new JsonArray(), ["loop"] = new JsonObject { ["unresolved"] = new JsonArray(), ["candidates"] = new JsonArray() },
            ["whole_layer"] = new JsonObject { ["status"] = "stale" } };
        PlanBlockers.Set(plan, [Perspective]);
        Routes.RefreshWholeLayer(plan);
        JsonObject wholeLayer = plan["whole_layer"]!.AsObject();
        Assert.Equal([BlockerCode.PerspectiveNeedsScreenspace], PlanBlockers.Codes(wholeLayer));
        Assert.True(JsonNode.DeepEquals(plan["blockers_localized"], wholeLayer["blockers_localized"]));
        Assert.NotSame(plan["blockers_localized"], wholeLayer["blockers_localized"]);
    }

    [Fact]
    public void PlaceLocalizedLastMovesOnlyTheLocalizedArray()
    {
        var plan = new JsonObject { ["blockers"] = new JsonArray(), ["loop"] = new JsonObject(), ["status"] = "x" };
        PlanBlockers.Set(plan, [Shell]);
        plan["daytime_split"] = new JsonObject();
        PlanBlockers.PlaceLocalizedLast(plan);
        Assert.Equal(["blockers", "loop", "status", "daytime_split", "blockers_localized"], plan.Select(pair => pair.Key));
        Assert.Equal([BlockerCode.VideoShell], PlanBlockers.Codes(plan));
    }

    [Fact]
    public void FinishOnlyAcceptsNodesBuiltOutsideAPlan()
    {
        var holder = new JsonObject { ["blockers"] = new JsonArray(Shell.ToNode()) };
        PlanBlockers.Finish(holder);
        Assert.Equal(Shell.Text, holder["blockers"]![0]!.GetValue<string>());
        Assert.Equal([BlockerCode.VideoShell], PlanBlockers.Codes(holder));
        // plan 从不持有节点：已是 v3 形态的数组再交给 Finish 就是用错了。
        Assert.Throws<InvalidDataException>(() => PlanBlockers.Finish(holder));
    }
}

[Trait("Layer", "L0")]
public class PlanWriterSourceRecordTests
{
    [Fact]
    public void PropertiesSourceIsReplacedInPlaceWhenAttachedAgain()
    {
        var report = new JsonObject { ["settings"] = new JsonObject(), ["snapshot_properties"] = new JsonObject(), ["projection"] = new JsonObject() };
        PlanWriter.AttachPropertiesSource(report, new JsonObject { ["source"] = "wpe", ["reason"] = "first" });
        PlanWriter.AttachPropertiesSource(report, new JsonObject { ["source"] = "defaults", ["reason"] = "second" });
        Assert.Equal(["settings", "snapshot_properties", "properties_source", "wpe_properties", "projection"], report.Select(pair => pair.Key));
        Assert.Equal("defaults", report["properties_source"]!.GetValue<string>());
        Assert.Equal("second", report["wpe_properties"]!["reason"]!.GetValue<string>());
    }

    [Fact]
    public void ConditionalUserBindingResolvesToWhetherTheValueMatches()
    {
        var properties = new JsonObject { ["mode"] = "night", ["scale"] = 3 };
        var conditional = new JsonObject { ["user"] = new JsonObject { ["name"] = "mode", ["condition"] = "night" }, ["value"] = false };
        var plain = new JsonObject { ["user"] = "scale", ["value"] = 1 };
        var missing = new JsonObject { ["user"] = "absent", ["value"] = 7 };
        Assert.Equal("true", SceneGraph.Resolve(conditional, properties)!.ToJsonString());
        Assert.Equal("false", SceneGraph.Resolve(conditional, new JsonObject { ["mode"] = "day" })!.ToJsonString());
        Assert.Equal("3", SceneGraph.Resolve(plain, properties)!.ToJsonString());
        Assert.Equal("7", SceneGraph.Resolve(missing, properties)!.ToJsonString());
    }
}

[Trait("Layer", "L0")]
public class PlanTransformsFreezeTests
{
    [Fact]
    public void FreezingResolvesOnlyUserBindingsWithoutScriptOrAnimation()
    {
        var scene = new JsonObject { ["objects"] = new JsonArray(new JsonObject {
            ["id"] = 1,
            ["effects"] = new JsonArray(new JsonObject {
                ["visible"] = new JsonObject { ["user"] = "show", ["value"] = true },
                ["passes"] = new JsonArray(new JsonObject { ["constantshadervalues"] = new JsonObject {
                    ["speed"] = new JsonObject { ["user"] = "speed", ["value"] = 1 },
                    ["scripted"] = new JsonObject { ["user"] = "speed", ["script"] = "x", ["value"] = 1 },
                    ["animated"] = new JsonObject { ["user"] = "speed", ["animation"] = new JsonObject(), ["value"] = 1 } } }) }),
            ["animationlayers"] = new JsonArray(new JsonObject { ["rate"] = new JsonObject { ["user"] = "rate", ["value"] = 1 } }) }) };
        PlanTransforms.FreezeTemporalProperties(scene, new JsonObject { ["show"] = false, ["speed"] = 4, ["rate"] = 2 });
        JsonNode effect = scene["objects"]![0]!["effects"]![0]!;
        JsonNode values = effect["passes"]![0]!["constantshadervalues"]!;
        Assert.Equal("false", effect["visible"]!.ToJsonString());
        Assert.Equal("4", values["speed"]!.ToJsonString());
        // 脚本或动画驱动的绑定随运行时变，快照值不算数，不冻结。
        Assert.IsType<JsonObject>(values["scripted"]);
        Assert.IsType<JsonObject>(values["animated"]);
        Assert.Equal("2", scene["objects"]![0]!["animationlayers"]![0]!["rate"]!.ToJsonString());
    }

    [Fact]
    public void NumericShaderConstantBoundToCheckboxFreezesAsOneOrZero()
    {
        // 数值常量绑到 bool 属性：WE 按 1/0 生效；combo 与 visible 仍冻结成 bool（渲染器与 ReadCombo 都认 bool）。
        JsonObject Pass() => new() {
            ["constantshadervalues"] = new JsonObject {
                ["speed"] = new JsonObject { ["user"] = "sun", ["value"] = 3 },
                ["alpha"] = new JsonObject { ["user"] = "slider", ["value"] = 1 } },
            ["combos"] = new JsonObject { ["NOISE"] = new JsonObject { ["user"] = "sun", ["value"] = 0 } } };
        var scene = new JsonObject { ["objects"] = new JsonArray(new JsonObject {
            ["id"] = 1, ["effects"] = new JsonArray(new JsonObject { ["passes"] = new JsonArray(Pass(), Pass()) }) }) };
        PlanTransforms.FreezeTemporalProperties(scene, new JsonObject { ["sun"] = true, ["slider"] = 0.5 });
        JsonNode pass = scene["objects"]![0]!["effects"]![0]!["passes"]![0]!;
        Assert.Equal("1", pass["constantshadervalues"]!["speed"]!.ToJsonString());
        // 周期分析不经序列化，直接按 double 读冻结后的常量。
        Assert.True(pass["constantshadervalues"]!["speed"]!.AsValue().TryGetValue(out double speed) && speed == 1);
        Assert.Equal("0.5", pass["constantshadervalues"]!["alpha"]!.ToJsonString());
        Assert.Equal("true", pass["combos"]!["NOISE"]!.ToJsonString());
        var off = new JsonObject { ["objects"] = new JsonArray(new JsonObject {
            ["id"] = 1, ["effects"] = new JsonArray(new JsonObject { ["passes"] = new JsonArray(Pass()) }) }) };
        PlanTransforms.FreezeTemporalProperties(off, new JsonObject { ["sun"] = false });
        Assert.Equal("0", off["objects"]![0]!["effects"]![0]!["passes"]![0]!["constantshadervalues"]!["speed"]!.ToJsonString());
    }
}
