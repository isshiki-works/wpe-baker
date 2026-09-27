using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

/// <summary>
/// 周期远超循环上限的 turbulence 共享场：整层路线冻结（timescale → 0）后按平稳粒子走交叉淡化，候选带补丁留给速度实测；
/// 实测没放行后的重分析照旧判不能；捕获时只改写这一层的粒子定义副本。
/// </summary>
[Trait("Layer", "L1")]
public class ParticleFieldFreezeTests
{
    [Fact]
    public Task Run() => TestTemp.Run(async root =>
    {
        string project = Path.Combine(root, "source");
        Directory.CreateDirectory(Path.Combine(project, "particles"));
        Directory.CreateDirectory(Path.Combine(project, "materials"));
        // 场周期 256 / (2 × 0.001 × 20) = 6400 s，是 600 s 上限的 2 倍以上：任何循环里离原速最近的圈数都是 0
        string definition = """
            {"emitter":[{"name":"boxrandom","rate":10}],"initializer":[{"name":"lifetimerandom","min":1,"max":2}],
             "operator":[{"name":"movement"},{"name":"turbulence","scale":0.001}],"maxcount":1000,"material":"materials/p.json"}
            """;
        File.WriteAllText(Path.Combine(project, "particles", "p.json"), definition);
        File.WriteAllText(Path.Combine(project, "materials", "p.json"), """{"passes":[{"shader":"genericparticle"}]}""");
        File.WriteAllText(Path.Combine(project, "scene.json"), """{"objects":[{"id":5,"particle":"particles/p.json"}]}""");
        File.WriteAllText(Path.Combine(project, "project.json"), """{"type":"scene","file":"scene.json"}""");
        using var source = new ProjectSource(project);
        JsonObject Scene() => JsonNode.Parse("""{"objects":[{"id":5,"particle":"particles/p.json"}]}""")!.AsObject();
        JsonObject Loop(bool budgetOnly) => LoopAnalysis.Analyze(Scene(), source, null, new JsonObject { ["runtime_dependencies"] = new JsonArray() },
            [5], 60, 1, videoGroups: new JsonArray(new JsonObject { ["id"] = "group-1", ["layer_ids"] = new JsonArray(5) }),
            budgetOnlyRetime: budgetOnly).ToJson();

        JsonObject frozen = Loop(false), refused = Loop(true);
        JsonObject? patch = frozen["candidates"]?.AsArray().FirstOrDefault()?["patches"]?.AsArray().OfType<JsonObject>()
            .SingleOrDefault(x => x["kind"]?.GetValue<string>() == LoopAnalysis.ParticleFieldPatchKind);
        Assert.True(patch is not null && patch["value_index"]!.GetValue<int>() == 1 && patch["old_value"]!.GetValue<double>() == 20 &&
            patch["new_value"]!.GetValue<double>() == 0, "冻结的湍流场给出 timescale 20 → 0 的补丁：" + frozen.ToJsonString());
        Assert.True(refused["candidates"]!.AsArray().Count == 0 && refused["unresolved"]!.ToJsonString().Contains("turbulence_shared_field", StringComparison.Ordinal),
            "实测没放行后的重分析照旧判不能：" + refused.ToJsonString());

        string capture = Path.Combine(root, "capture");
        Directory.CreateDirectory(capture);
        JsonObject scene = Scene();
        await HybridLoopService.WriteParticleFieldsAsync(capture, source, null, scene, frozen, CancellationToken.None);
        string patched = scene["objects"]![0]!["particle"]!.GetValue<string>();
        JsonObject written = JsonNode.Parse(File.ReadAllText(ProjectSource.ContainedPath(capture, patched)))!.AsObject();
        Assert.True(patched != "particles/p.json" && written["operator"]![1]!["timescale"]!.GetValue<double>() == 0 &&
            written["operator"]![0]!["timescale"] is null && File.ReadAllText(Path.Combine(project, "particles", "p.json")) == definition,
            "捕获副本里这一层改指向冻结了场的定义副本，原定义不动：" + written.ToJsonString());
    });
}
