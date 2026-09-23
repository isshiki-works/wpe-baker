using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// 把"没有规则认得这个 shader"与"这个 shader 没有可用周期"分开。裁定断言（片段逐字取自官方源码）在
/// tests/corpus/shader-corpus.json 的 split-period；这里只留跨模块断言：求解器、运行时材质投影与效果前缀。
/// </summary>
internal static class ShaderSplitPeriodChecks
{
    internal static void Run(Action<bool, string> check, string root)
    {
        ShaderCorpusRun run = ShaderCorpusChecks.Run(check, root, "split-period");
        using var source = new ProjectSource(run.Directory);
        JsonObject Owner(int id, string name, JsonObject? values = null, JsonObject? combos = null)
        {
            var pass = new JsonObject();
            if (values is not null) pass["constantshadervalues"] = values;
            if (combos is not null) pass["combos"] = combos;
            return new JsonObject { ["id"] = id, ["effects"] = new JsonArray(new JsonObject {
                ["file"] = $"effects/{name}.json", ["passes"] = new JsonArray(pass) }) };
        }


        // The fixed-period grain must reach the solver as a locked constraint with no scene patch.
        var grainScene = new JsonObject { ["objects"] = new JsonArray(
            Owner(1, "filmgrain", new JsonObject { ["scale"] = 10.0 })) };
        JsonObject report = HybridLoopService.Analyze(grainScene, source, null, new JsonObject(), [1], 60, 1);
        JsonObject candidate = report["candidates"]!.AsArray().First()!.AsObject();
        ulong grainFrames = candidate["frames"]!.GetValue<ulong>();
        check(grainFrames % 60 == 0 && candidate["patches"]!.AsArray().Count == 0 &&
            candidate["components"]![0]!["cycles"]!.GetValue<ulong>() == grainFrames / 60 &&
            candidate["components"]![0]!["delta_percent"]!.GetValue<double>() == 0,
            "a literal-rate fixed period closes on whole seconds and emits no capture-scene patch");

        // 一个图层的时间行为由「哪个 shader、哪套常量」决定，不由它在运行时以 source 还是 effect 角色实例化
        // 决定。direct-draw 效果层（shape quad、没有 image、DIRECTDRAW=1）会把同一个 effect 实例化成两份材质，
        // 两份的 active_uniforms 完全一样；同一层同一 shader 只能被裁定一次。
        JsonObject Material(string shader, string role, params string[] uniforms) => new()
        {
            ["shader"] = shader, ["role"] = role,
            ["active_uniforms"] = new JsonArray(uniforms.Select(name => (JsonNode)JsonValue.Create(name)).ToArray()),
            ["textures"] = new JsonArray()
        };
        JsonObject RuntimeLayer(int owner, params JsonObject[] materials) => new()
        {
            ["owner"] = owner, ["has_mesh"] = true,
            ["materials"] = new JsonArray(materials.Cast<JsonNode?>().ToArray())
        };
        JsonObject Runtime(params JsonObject[] layers) => new()
        {
            ["status"] = "complete", ["runtime_dependencies"] = new JsonArray(), ["runtime_animation_periods"] = new JsonArray(),
            ["runtime_layers"] = new JsonArray(layers.Cast<JsonNode?>().ToArray())
        };
        JsonObject directDrawOwner = Owner(83, "lightshafts", new JsonObject { ["rayspeed"] = 0.38999999 },
            new JsonObject { ["DIRECTDRAW"] = 1, ["RAYCORNER"] = 1, ["RAYMODE"] = 2 });
        directDrawOwner["shape"] = "quad";
        var directDrawScene = new JsonObject { ["objects"] = new JsonArray(directDrawOwner) };
        JsonObject directDraw = HybridLoopService.Analyze(directDrawScene, source, null,
            Runtime(RuntimeLayer(83, Material("lightshafts", "source", "g_Speed", "g_Time"),
                Material("lightshafts", "effect", "g_Speed", "g_Time"))), [83], 60, 1);
        JsonObject[] directDrawUnresolved = directDraw["unresolved"]!.AsArray().OfType<JsonObject>().ToArray();
        check(directDrawUnresolved.Length == 1 &&
            directDrawUnresolved[0]["kind"]?.GetValue<string>() == "NonPeriodicOrDriftingMechanism" &&
            directDrawUnresolved[0]["owner_layer_id"]?.GetValue<int>() == 83 &&
            directDrawUnresolved[0]["mechanism"]?.GetValue<string>() == ShaderPeriodAnalysis.LightShaftDriftMechanism &&
            directDrawUnresolved[0]["bounded_displacement"]?.GetValue<bool>() == false,
            "a direct-draw effect layer instantiated as both source and effect materials reports exactly one unresolved component");

        // 反例一：同一层有个时钟材质，但它的 shader 从没被方程规则裁定过——必须照旧报 runtime_material。
        JsonObject unanalysed = HybridLoopService.Analyze(directDrawScene, source, null,
            Runtime(RuntimeLayer(83, Material("lightshafts", "effect", "g_Time"),
                Material("workshop/custom_clock", "source", "g_Time"))), [83], 60, 1);
        check(unanalysed["unresolved"]!.AsArray().OfType<JsonObject>().Count(item =>
                item["kind"]?.GetValue<string>() == "runtime_material" && item["owner_layer_id"]?.GetValue<int>() == 83) == 1,
            "a runtime material whose shader carries no shader-analysis verdict still reports its unmodeled clock");

        // 反例二（跨层）：键必须是 (层, shader) 二元组。84 层挂的是同名 shader，但那一层没有任何裁定，
        // 83 层的裁定不能借给它。
        JsonObject crossLayerScene = new JsonObject { ["objects"] = new JsonArray(directDrawOwner.DeepClone(),
            new JsonObject { ["id"] = 84, ["shape"] = "quad" }) };
        JsonObject crossLayer = HybridLoopService.Analyze(crossLayerScene, source, null,
            Runtime(RuntimeLayer(83, Material("lightshafts", "source", "g_Time"), Material("lightshafts", "effect", "g_Time")),
                RuntimeLayer(84, Material("lightshafts", "effect", "g_Time"))), [83, 84], 60, 1);
        JsonObject[] crossUnresolved = crossLayer["unresolved"]!.AsArray().OfType<JsonObject>().ToArray();
        check(crossUnresolved.Count(item => item["kind"]?.GetValue<string>() == "runtime_material" &&
                item["owner_layer_id"]?.GetValue<int>() == 84) == 1 &&
            crossUnresolved.All(item => !(item["kind"]?.GetValue<string>() == "runtime_material" &&
                item["owner_layer_id"]?.GetValue<int>() == 83)),
            "a shader verdict on one layer is never borrowed by the same shader name on another layer");

        // 前缀分析（effect_prefix 路线）只覆盖前 N 个效果，运行时证据必须跟着一起投影：被截掉的效果
        // 没有对应的方程裁定，留在证据里就会以「未建模时钟」的名义反过来否掉这个前缀。
        var prefixOwner = new JsonObject
        {
            ["id"] = 30,
            ["effects"] = new JsonArray(
                new JsonObject { ["file"] = "effects/filmgrain.json",
                    ["passes"] = new JsonArray(new JsonObject { ["constantshadervalues"] = new JsonObject { ["scale"] = 10.0 } }) },
                new JsonObject { ["file"] = "effects/lightshafts.json",
                    ["passes"] = new JsonArray(new JsonObject { ["constantshadervalues"] = new JsonObject { ["rayspeed"] = 0.2 } }) })
        };
        var analyzePrefix = typeof(HybridLoopService).Assembly.GetType("Baker.Core.EffectPrefixPlanner")!
            .GetMethod("AnalyzePrefix", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        // 前缀分析现在按请求走统一入口；这一组断言只看运行时证据的投影，请求取"没选档"的旧行为（60 fps、2% 通用预算、600 s）。
        var prefixRequest = new HybridAnalyzeRequest(2, "source", "assets", "output", FpsNumerator: 60, FpsDenominator: 1);
        JsonObject Prefix(JsonObject prefixRuntime, int prefixCount) => (JsonObject)analyzePrefix.Invoke(null, [
            new JsonObject { ["objects"] = new JsonArray(prefixOwner.DeepClone()) }, source, null, prefixRuntime,
            new JsonObject(), 30, prefixCount, prefixRequest, new JsonObject()])!;
        JsonObject prefixRuntime = Runtime(RuntimeLayer(30, Material("filmgrain", "effect", "g_Time"),
            Material("lightshafts", "effect", "g_Speed", "g_Time")));
        JsonObject firstEffectOnly = Prefix(prefixRuntime, 1);
        check(firstEffectOnly["unresolved"]!.AsArray().Count == 0 &&
            firstEffectOnly["candidates"]!.AsArray().Count > 0,
            "an effect prefix keeps its closed loop when a later effect's runtime material is outside the analysed prefix");
        JsonObject wholeChain = Prefix(prefixRuntime, 2);
        JsonObject[] wholeChainUnresolved = wholeChain["unresolved"]!.AsArray().OfType<JsonObject>().ToArray();
        check(wholeChainUnresolved.Length == 1 &&
            wholeChainUnresolved[0]["mechanism"]?.GetValue<string>() == ShaderPeriodAnalysis.LightShaftDriftMechanism,
            "a prefix that includes the drifting effect still reports it once, through its shader equation");
        // 反例：既不属于前缀、也不属于任何被截效果的时钟材质仍然必须报出来，剔除是按被截效果的 shader 精确做的。
        JsonObject strayClock = Prefix(Runtime(RuntimeLayer(30, Material("filmgrain", "effect", "g_Time"),
            Material("lightshafts", "effect", "g_Time"), Material("workshop/custom_clock", "source", "g_Time"))), 1);
        check(strayClock["unresolved"]!.AsArray().OfType<JsonObject>().Count(item =>
                item["kind"]?.GetValue<string>() == "runtime_material") == 1,
            "a runtime clock material that belongs to no analysed or dropped effect still blocks the prefix");
    }
}
