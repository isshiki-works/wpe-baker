using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// 粒子的音频响应是实时外部输入，与着色器音频频谱同级：输入驱动的层留实时、不进烘焙。
/// audioprocessingmode 可以出现在 emitter、initializer 或 operator 任一节点上，都算数；
/// 值为 0 时音频是关的，不能借这条规则把层改判实时。
/// </summary>
internal static class ParticleRealtimeChecks
{
    internal static async Task RunAsync(Action<bool, string> check, string root)
    {
        string sourceDirectory = Path.Combine(root, "particle-realtime-source");
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "particles"));

        JsonObject Definition(int emitterMode, int initializerMode, int operatorMode) => new() {
            ["material"] = "materials/particle/halo.json", ["maxcount"] = 50,
            ["emitter"] = new JsonArray(new JsonObject { ["name"] = "boxrandom", ["rate"] = 5,
                ["distancemin"] = "0 0 0", ["distancemax"] = "0 0 0", ["audioprocessingmode"] = emitterMode }),
            ["initializer"] = new JsonArray(
                new JsonObject { ["name"] = "lifetimerandom", ["min"] = 3, ["max"] = 3 },
                new JsonObject { ["name"] = "velocityrandom", ["min"] = "20 20 0", ["max"] = "20 20 0",
                    ["audioprocessingmode"] = initializerMode }),
            ["operator"] = new JsonArray(new JsonObject { ["name"] = "movement", ["gravity"] = "0 0 0",
                ["audioprocessingmode"] = operatorMode }) };
        async Task WriteAsync(string name, JsonObject definition) =>
            await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "particles", name), definition.ToJsonString());
        await WriteAsync("silent.json", Definition(0, 0, 0));
        await WriteAsync("emitter-audio.json", Definition(3, 0, 0));
        await WriteAsync("initializer-audio.json", Definition(0, 2, 0));
        await WriteAsync("operator-audio.json", Definition(0, 0, 1));

        // 三层：全幅底图、静音粒子（对照组）、被检粒子。只换被检粒子的定义文件，其余保持不变。
        static JsonArray Objects(string probed) => new(
            new JsonObject { ["id"] = 10, ["name"] = "背景", ["image"] = "models/background.json",
                ["size"] = "64 32", ["origin"] = "32 16 0" },
            new JsonObject { ["id"] = 20, ["name"] = "silent strings", ["particle"] = "particles/silent.json",
                ["origin"] = "32 16 0" },
            new JsonObject { ["id"] = 30, ["name"] = "probed strings", ["particle"] = "particles/" + probed,
                ["origin"] = "32 16 0" });
        string scenePath = Path.Combine(sourceDirectory, "scene.json");
        async Task WriteSceneAsync(string probed) => await File.WriteAllTextAsync(scenePath, new JsonObject {
            ["general"] = new JsonObject {
                ["orthogonalprojection"] = new JsonObject { ["width"] = 64, ["height"] = 32 },
                ["clearenabled"] = true },
            ["objects"] = Objects(probed) }.ToJsonString());
        await WriteSceneAsync("emitter-audio.json");
        string tracePath = Path.Combine(root, "particle-realtime-trace.json");
        await File.WriteAllTextAsync(tracePath, new JsonObject {
            ["source"] = scenePath, ["status"] = "complete",
            ["runtime_dependencies"] = new JsonArray(),
            ["source_script_error_count"] = 0, ["source_script_errors"] = new JsonArray(),
            ["runtime_layers"] = new JsonArray(Objects("emitter-audio.json").OfType<JsonObject>().Select(obj => (JsonNode)new JsonObject {
                ["id"] = obj["id"]!.DeepClone(), ["owner"] = obj["id"]!.DeepClone(), ["visible"] = true,
                ["has_mesh"] = true, ["effective_parallax_depth"] = new JsonArray(0, 0),
                ["materials"] = new JsonArray(new JsonObject { ["uses_audio_spectrum"] = false, ["textures"] = new JsonArray() })
            }).ToArray()) }.ToJsonString());

        async Task<JsonObject> PlanAsync(string name) =>
            await new HybridScenePlanner(new("not-started", "not-started", "not-started", [])).AnalyzeAsync(
                new(2, sourceDirectory, root, Path.Combine(root, name), 64, 32,
                    RuntimeTraceFile: tracePath, VideoLayout: "layered"));
        static JsonObject Layer(JsonObject plan, int id) => plan["layers"]!.AsArray().OfType<JsonObject>()
            .Single(layer => layer["id"]!.GetValue<int>() == id);
        static bool AudioLive(JsonObject plan, int id) => Layer(plan, id)["allocation"]!.GetValue<string>() == "live" &&
            Layer(plan, id)["reasons"]!.AsArray().Any(reason => reason!.GetValue<string>() == "particle_audio_input");

        JsonObject emitterPlan = await PlanAsync("particle-realtime-emitter");
        check(AudioLive(emitterPlan, 30) &&
            emitterPlan["live_layer_ids"]!.AsArray().Select(id => id!.GetValue<int>()).SequenceEqual(new[] { 30 }) &&
            Layer(emitterPlan, 20)["allocation"]!.GetValue<string>() == "video" &&
            !Layer(emitterPlan, 20)["reasons"]!.AsArray().Any(reason => reason!.GetValue<string>() == "particle_audio_input") &&
            Layer(emitterPlan, 10)["allocation"]!.GetValue<string>() == "video",
            "a particle emitter with a non-zero audioprocessingmode stays live as particle_audio_input while a silent particle system does not");

        await WriteSceneAsync("initializer-audio.json");
        JsonObject initializerPlan = await PlanAsync("particle-realtime-initializer");
        await WriteSceneAsync("operator-audio.json");
        JsonObject operatorPlan = await PlanAsync("particle-realtime-operator");
        check(AudioLive(initializerPlan, 30) && AudioLive(operatorPlan, 30) &&
            Layer(initializerPlan, 20)["allocation"]!.GetValue<string>() == "video" &&
            Layer(operatorPlan, 20)["allocation"]!.GetValue<string>() == "video",
            "a non-zero audioprocessingmode on an initializer or an operator node is the same real-time audio input as on the emitter");
    }
}
