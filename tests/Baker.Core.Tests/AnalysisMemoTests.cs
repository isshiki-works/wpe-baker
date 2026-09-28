using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Baker.Core;
using Xunit;

// 分析提速：一次 analyze 内的记忆（AnalysisMemo）与退回轮内并行。两者都不许改结论与 plan。
[Trait("Layer", "L1")]
public class AnalysisMemoTests
{
    [Fact]
    public async Task ParallelAdmissionRetreatSelectsExactlyWhatSerialRetreatSelects()
    {
        // 四组里只有把12留实时才能解开生成准入；并行完成顺序与组序相反，也必须选中同一方案。
        await TestTemp.Run(async root =>
        {
            int active = 0, peak = 0;
            async Task<JsonObject> Analyze(HybridAnalyzeRequest r)
            {
                Interlocked.Exchange(ref peak, Math.Max(peak, Interlocked.Increment(ref active)));
                int[] kept = r.RetainLiveRootIds ?? [];
                await Task.Delay(kept.Length == 0 ? 0 : 40 * (14 - kept.Max()));
                Interlocked.Decrement(ref active);
                int[] live = [.. new[] { 10, 11, 12, 13 }.Except(kept)];
                var plan = new JsonObject
                {
                    ["summary"] = new JsonObject { ["key"] = kept.Contains(12) ? "summary.bakeable" : "summary.blocked", ["zh"] = "", ["en"] = "" },
                    ["settings"] = new JsonObject { ["preset"] = r.Preset, ["interaction"] = r.Interaction,
                        ["retain_live_root_ids"] = new JsonArray([.. kept.Select(id => (JsonNode)id)]) },
                    ["route"] = "whole_layer",
                    ["output"] = r.OutputDirectory,
                    ["video_groups"] = new JsonArray([.. live.Select(id => (JsonNode)new JsonObject { ["id"] = id, ["root_ids"] = new JsonArray(id) })]),
                    ["blockers"] = new JsonArray(),
                    ["loop"] = new JsonObject { ["candidates"] = new JsonArray(new JsonObject { ["frames"] = 600 }) },
                    ["bake_value"] = new JsonObject { ["rule"] = WorkloadValue.CachedEffectPasses.Rule,
                        ["evidence"] = new JsonObject { ["effect_pass_coverage"] = 2.0 } }
                };
                if (!kept.Contains(12)) PlanBlockers.Add(plan, new Blocker(BlockerCode.BakeAllocation, ["group 12 blocks generation"]));
                return plan;
            }
            async Task<string> RunAsync(string name, int parallelism)
            {
                string output = Path.Combine(root, name);
                JsonObject result = await AnalysisOrchestrator.RunAsync(new(2, "s", "a", output), (r, _) => Analyze(r), CancellationToken.None,
                    memo: new AnalysisMemo(parallelism));
                // 分阶段计时是墙钟，串行与并行两次必然不同，比较前去掉。
                result.Remove("analysis_timing");
                return Regex.Replace(result.ToJsonString().Replace(output.Replace("\\", "\\\\"), "<out>"), "run-[0-9a-f]{32}", "run");
            }
            string serial = await RunAsync("serial", 1);
            Assert.Equal(1, peak);
            peak = 0;
            string parallel = await RunAsync("parallel", 8);
            Assert.True(peak > 1);
            Assert.Equal(serial, parallel);
            JsonObject chosen = JsonNode.Parse(parallel)!.AsObject();
            Assert.Equal([12], chosen["settings"]!["retain_live_root_ids"]!.AsArray().Select(n => n!.GetValue<int>()));
            Assert.Contains("retreat-3", chosen["output"]!.GetValue<string>());
        });
    }

    [Fact]
    public async Task SharedInvariantsGiveTheSamePlansAsSeparateAnalyses()
    {
        // 同一个记忆里依次分析：留实时 3（分配往实时判定里加 retained_by_cost_trial）、不留、换成保留视角（parallaxDepth 的写使 3 实时）。
        // 每一份都要与不带记忆单独分析的 plan 相同：母本被改写或键漏了 ViewMode 都会串结果。
        await TestTemp.Run(async root =>
        {
            string source = await FixtureAsync(root);
            var planner = new HybridScenePlanner(new("not-started", "not-started", "not-started", []));
            var memo = new AnalysisMemo();
            HybridAnalyzeRequest[] requests = [
                Request(root, source) with { ViewMode = "fixed_view", RetainLiveRootIds = [3] },
                Request(root, source) with { ViewMode = "fixed_view" },
                Request(root, source) with { ViewMode = "preserve" }];
            for (int index = 0; index < requests.Length; index++)
            {
                string shared = Path.Combine(root, "shared-" + index), alone = Path.Combine(root, "alone-" + index);
                JsonObject withMemo = await planner.AnalyzeSingleAsync(requests[index] with { OutputDirectory = shared }, memo, null, CancellationToken.None);
                JsonObject without = await planner.AnalyzeSingleAsync(requests[index] with { OutputDirectory = alone });
                Assert.Equal(Normalize(without, alone), Normalize(withMemo, shared));
            }
            Assert.Contains("runtime_parallax_depth_change", Layer(await planner.AnalyzeSingleAsync(requests[2] with {
                OutputDirectory = Path.Combine(root, "preserve") }), 3)["reasons"]!.ToJsonString());
        });
    }

    [Fact]
    public async Task InvariantsAreComputedOncePerAnalysis()
    {
        // 记忆真的复用：第一次分析之后改掉源，同一个记忆里的第二次仍用第一次的 scene 与源哈希，收尾核对报"分析期间源被改过"。
        await TestTemp.Run(async root =>
        {
            string source = await FixtureAsync(root);
            var planner = new HybridScenePlanner(new("not-started", "not-started", "not-started", []));
            var memo = new AnalysisMemo();
            JsonObject first = await planner.AnalyzeSingleAsync(Request(root, source) with { OutputDirectory = Path.Combine(root, "first") }, memo, null, CancellationToken.None);
            string scene = Path.Combine(source, "scene.json");
            await File.WriteAllTextAsync(scene, (await File.ReadAllTextAsync(scene)).Replace("Label A", "Label C"));
            JsonObject second = await planner.AnalyzeSingleAsync(Request(root, source) with { OutputDirectory = Path.Combine(root, "second") }, memo, null, CancellationToken.None);
            Assert.Equal(first["source_sha256"]!.GetValue<string>(), second["source_sha256"]!.GetValue<string>());
            Assert.Equal("Label A", Layer(second, 3)["name"]!.GetValue<string>());
            await Assert.ThrowsAsync<IOException>(() => memo.VerifySourcesAsync(CancellationToken.None));

            // 逐组静态证明按 (源, 运行时证据内容, 图层, 帧率) 记住：证过一次后源没了也不再打开它；不带记忆时照旧打开，读不到就记 unavailable。
            JsonObject Budget(string name)
            {
                string runtime = Path.Combine(root, name + "-runtime.json");
                File.Copy(Path.Combine(root, "trace.json"), runtime);
                return new JsonObject {
                    ["summary"] = new JsonObject { ["key"] = "summary.bakeable" }, ["route"] = "whole_layer", ["blockers"] = new JsonArray(),
                    ["loop"] = new JsonObject { ["candidates"] = new JsonArray(new JsonObject { ["frames"] = 600 }) },
                    ["source"] = scene, ["source_sha256"] = "fixture", ["runtime_evidence"] = runtime, ["settings"] = new JsonObject(),
                    ["video_groups"] = new JsonArray(new JsonObject { ["id"] = "g0", ["layer_ids"] = new JsonArray(1) }) };
            }
            Assert.Equal("verified", (await AnalysisOrchestrator.AdoptAllocationAsync(Budget("a"), CancellationToken.None, memo))["static_group_budget"]!["status"]!.GetValue<string>());
            File.Delete(scene);
            Assert.Equal("verified", (await AnalysisOrchestrator.AdoptAllocationAsync(Budget("b"), CancellationToken.None, memo))["static_group_budget"]!["status"]!.GetValue<string>());
            Assert.Equal("unavailable", (await AnalysisOrchestrator.AdoptAllocationAsync(Budget("c"), CancellationToken.None))["static_group_budget"]!["status"]!.GetValue<string>());
        });
    }

    internal static HybridAnalyzeRequest Request(string root, string source) =>
        new(2, source, root, "", 64, 32, RuntimeTraceFile: Path.Combine(root, "trace.json"));

    internal static JsonObject Layer(JsonObject plan, int id) =>
        plan["layers"]!.AsArray().OfType<JsonObject>().Single(layer => layer["id"]!.GetValue<int>() == id);

    private static string Normalize(JsonObject plan, string output) => plan.ToJsonString().Replace(output.Replace("\\", "\\\\"), "<out>");

    /// <summary>不透明底图、读音频频谱的实时层、两个小文本层；实时层运行时写 3 的 parallaxDepth（保留视角时 3 因此实时）。</summary>
    internal static async Task<string> FixtureAsync(string root)
    {
        string directory = Path.Combine(root, "source");
        Directory.CreateDirectory(directory);
        var objects = new JsonArray(
            new JsonObject { ["id"] = 1, ["name"] = "Base plate", ["text"] = "base", ["size"] = new JsonArray(64, 32) },
            new JsonObject { ["id"] = 2, ["name"] = "Audio spectrum", ["image"] = "models/spectrum.json", ["size"] = new JsonArray(32, 16) },
            new JsonObject { ["id"] = 3, ["name"] = "Label A", ["text"] = "a", ["size"] = new JsonArray(8, 4) },
            new JsonObject { ["id"] = 4, ["name"] = "Label B", ["text"] = "b", ["size"] = new JsonArray(8, 4) });
        await File.WriteAllTextAsync(Path.Combine(directory, "scene.json"), new JsonObject {
            ["general"] = new JsonObject { ["orthogonalprojection"] = new JsonObject { ["width"] = 64, ["height"] = 32 }, ["clearenabled"] = true },
            ["objects"] = objects.DeepClone() }.ToJsonString());
        await File.WriteAllTextAsync(Path.Combine(root, "trace.json"), new JsonObject {
            ["source"] = Path.Combine(directory, "scene.json"), ["status"] = "complete",
            ["source_script_error_count"] = 0, ["source_script_errors"] = new JsonArray(),
            ["runtime_dependencies"] = new JsonArray(new JsonObject { ["owner"] = 2, ["target"] = 3, ["operation"] = "write",
                ["property"] = "parallaxDepth", ["initialization"] = false }),
            ["runtime_layers"] = new JsonArray([.. objects.OfType<JsonObject>().Select(layer => (JsonNode)new JsonObject {
                ["id"] = layer["id"]!.DeepClone(), ["owner"] = layer["id"]!.DeepClone(), ["visible"] = true,
                ["has_mesh"] = true, ["effective_parallax_depth"] = new JsonArray(0, 0),
                ["materials"] = new JsonArray(new JsonObject {
                    ["uses_audio_spectrum"] = layer["id"]!.GetValue<int>() == 2, ["textures"] = new JsonArray() }) })]) }.ToJsonString());
        return directory;
    }
}
