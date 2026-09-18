using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// Separates "no rule recognises this shader" from "this shader has no usable period". Every
/// fragment below is copied verbatim out of the shipped shader source that blocked the fixed
/// ten-wallpaper set, so the checks fail if a rule stops matching the real family.
/// </summary>
internal static class ShaderSplitPeriodChecks
{
    internal static void Run(Action<bool, string> check, string root)
    {
        string sourceDirectory = Path.Combine(root, "shader-split-period-source");
        void Write(string relative, string text)
        {
            string path = Path.Combine(sourceDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        void Shader(string name, string fragment, string vertex)
        {
            Write($"effects/{name}.json", $"{{\"passes\":[{{\"material\":\"materials/{name}.json\"}}]}}");
            Write($"materials/{name}.json", $"{{\"passes\":[{{\"shader\":\"{name}\"}}]}}");
            Write($"shaders/{name}.frag", fragment);
            Write($"shaders/{name}.vert", vertex);
        }
        JsonObject Owner(int id, string name, JsonObject? values = null, JsonObject? combos = null)
        {
            var pass = new JsonObject();
            if (values is not null) pass["constantshadervalues"] = values;
            if (combos is not null) pass["combos"] = combos;
            return new JsonObject { ["id"] = id, ["effects"] = new JsonArray(new JsonObject {
                ["file"] = $"effects/{name}.json", ["passes"] = new JsonArray(pass) }) };
        }

        // effects/filmgrain: the only clock use is frac(g_Time) in the vertex stage.
        const string GrainFragment = """
            uniform sampler2D g_Texture1; // {"label":"ui_editor_properties_noise","default":"util/noise"}
            void main() {
            	vec3 noise = texSample2D(g_Texture1, v_TexCoordNoise.xy).rgb;
            	gl_FragColor = vec4(noise, 1.0);
            }
            """;
        const string GrainVertex = """
            uniform float g_Time;
            uniform float g_NoiseScale; // {"material":"scale","label":"ui_editor_properties_scale","default":10,"range":[0.0, 20.0]}
            void main() {
            	float t = frac(g_Time);
            	v_TexCoord = a_TexCoord.xyxy;
            	v_TexCoordNoise.xy = (a_TexCoord.xy + t) * g_NoiseScale;
            	v_TexCoordNoise.zw = (a_TexCoord.xy - t * 2.5) * g_NoiseScale * 0.52;
            	v_TexCoordNoise *= vec4(aspect, 1.0, aspect, 1.0);
            }
            """;
        Shader("filmgrain", GrainFragment, GrainVertex);
        // A second clock term outside frac(g_Time) must fall back to the generic refusal.
        Shader("filmgrain-extra", GrainFragment, GrainVertex + "\nv_TexCoord.x += g_Time * 0.125;");

        // effects/workshop crt_screen: scan line wraps at 1.0 and flicker at 2.0 over the same clock.
        const string CrtFragment = """
            // [COMBO] {"material":"Frequency artifacts","combo":"ARTIFACTS","type":"options","default":1}
            uniform float u_frequency; // {"material":"Frequency","default":10,"range":[-30,30],"group":"Screen"}
            uniform float g_Time;
            void main() {
            #if ARTIFACTS
                    float speed = g_Time * u_frequency;
                    albedo.rgb *= 1.0 - min(1.0, mod(1.0 - uv.y * u_amount1 + speed, 1.0) * u_size1) * u_strength1; //Scan line
                    albedo.rgb *= 1.0 - abs(mod(1.0 - (uv.y + u_offset2) * u_amount2 + speed, 2.0) - 1.0) * u_strength2; //Flicker
            #endif
            }
            """;
        Shader("crt_screen", CrtFragment, "uniform mat4 g_ModelViewProjectionMatrix;");

        // effects/lightshafts: two noise lookups translate at four unrelated rates.
        const string ShaftFragment = """
            uniform float g_Time;
            uniform float g_Speed; // {"material":"rayspeed","label":"ui_editor_properties_speed","default":0.2,"range":[0.1, 1.0],"group":"ui_editor_properties_shape"}
            void main() {
            	fxCoord.xy += g_Time * g_Speed * vec2(0.003, 0.000375111);
            	fxCoord2.xy -= g_Time * g_Speed * vec2(0.0047111, 0.0007399);
            	float fx0 = texSample2D(g_Texture1, fxCoord).r;
            }
            """;
        Shader("lightshafts", ShaftFragment, "uniform mat4 g_ModelViewProjectionMatrix;");

        // effects/iris: an integer step index feeds sine hashes with irrational frequencies.
        const string IrisVertex = """
            uniform float g_Time;
            uniform float g_Speed; // {"material":"speed","label":"ui_editor_properties_speed","default":1,"range":[0.01, 2.0]}
            void main() {
            	float time = (g_Time * g_Speed) + g_PhaseOffset;
            	float lowDt = floor(time);
            	vec2 motion2 = sin(1.9 * (lowDt + vec2(0, 1)));
            	vec4 motion4 = sin(2.5 * (lowDt + vec4(0, 0, 1, 1)) + vec4(1, 2, 1, 2));
            	vec2 da = mix(moveStart, moveEnd, smoothstep(1 - g_Rough, 1, cos(frac(time) * M_PI) * -0.5 + 0.5));
            	da.x += sin(time) * g_NoiseAmount;
            	da.y += cos(time) * g_NoiseAmount;
            }
            """;
        Shader("iris", "void main() { gl_FragColor = texSample2D(g_Texture0, v_TexCoord.xy); }", IrisVertex);

        // effects/foliagesway: both stages keep the same eight-term sway series.
        const string FoliageFragment = """
            uniform float g_Speed; // {"material":"speeduv","label":"ui_editor_properties_speed","default":5,"range":[0.01, 20]}
            uniform float g_Time;
            void main() {
            	vec4 sines = phase + g_Speed * g_Time * vec4(1, -0.16161616, 0.0083333, -0.00019841);
            	vec4 csines = 0.4 + phase + g_Speed * g_Time * vec4(-0.5, 0.041666666, -0.0013888889, 0.000024801587);
            }
            """;
        const string FoliageVertex = """
            uniform float g_Time;
            uniform float g_Speed; // {"material":"speed","label":"ui_editor_properties_speed","default":1,"range":[0.01, 10]}
            void main() {
            	vec4 sines = g_Phase + g_Speed * g_Time * vec4(1, -0.16161616, 0.0083333, -0.00019841);
            	vec4 csines = 0.4 + g_Phase + g_Speed * g_Time * vec4(-0.5, 0.041666666, -0.0013888889, 0.000024801587);
            }
            """;
        Shader("foliagesway", FoliageFragment, FoliageVertex);

        // effects/workshop shadow_map: frac(p * 0.1031) makes the static-noise hash 10000/1031 periodic.
        const string ShadowFragment = """
            uniform float g_Time;
            uniform float u_noise; // {"material":"Static noise","default":0.5,"range":[0,1],"group":"Imperfections"}
            float hash(vec2 p){
            	vec3 p3 = frac(CAST3(p.xyx) * 0.1031);
                p3 += dot(p3, p3.yzx + 33.33);
                return frac((p3.x + p3.y) * p3.z);
            }
            void main() {
                    albedo.rgb *= 1.0 - hash(round(uv) + g_Time) * u_noise; //Static noise
            }
            """;
        Shader("shadow_map", ShadowFragment, "uniform mat4 g_ModelViewProjectionMatrix;");

        var scene = new JsonObject { ["objects"] = new JsonArray(
            Owner(1, "filmgrain", new JsonObject { ["scale"] = 10.0, ["exponent"] = 0.44999999 }, new JsonObject { ["GREYSCALE"] = 0 }),
            Owner(2, "filmgrain-extra", new JsonObject { ["scale"] = 10.0 }),
            Owner(3, "crt_screen", new JsonObject { ["Frequency"] = -30.0, ["Scan line count"] = 0.28 }),
            Owner(4, "crt_screen", new JsonObject { ["Frequency"] = -30.0 }, new JsonObject { ["ARTIFACTS"] = 0 }),
            Owner(5, "lightshafts", new JsonObject { ["rayspeed"] = 0.2, ["raysmoothness"] = 0.58999997 }),
            Owner(6, "iris", new JsonObject { ["speed"] = 1.0, ["phase"] = 0.0, ["rough"] = 0.2, ["noiseamount"] = 0.5 }),
            Owner(7, "foliagesway", new JsonObject { ["speeduv"] = 5.0, ["phase"] = 0.5, ["power"] = 1.0 }),
            Owner(8, "shadow_map", new JsonObject { ["Static noise"] = 0.15000001, ["Vignette"] = 1.0700001 }),
            Owner(9, "shadow_map", new JsonObject { ["Static noise"] = 0.0 }),
            Owner(10, "foliagesway", new JsonObject { ["speeduv"] = 5.0, ["strength"] = 0.0 }),
            Owner(11, "foliagesway", new JsonObject { ["speeduv"] = 0.0, ["strength"] = 0.52999997 }),
            Owner(12, "foliagesway", new JsonObject { ["speed"] = 5.0, ["strength"] = 0.52999997,
                ["directionweights"] = "0 0" }, new JsonObject { ["MODE"] = 1 }),
            Owner(13, "foliagesway", new JsonObject { ["speed"] = 5.0, ["strength"] = 0.52999997,
                ["directionweights"] = "1 0.2" }, new JsonObject { ["MODE"] = 1 }),
            // WE 编辑器把 combo 绑定到 bool 用户属性时写出 JSON true/false；冻结后的场景里同一个
            // combo 也可能是 1.0 或 "1"。这些都必须和 int 1/0 得到同样的判定，读不出来的值必须拒绝。
            Owner(14, "crt_screen", new JsonObject { ["Frequency"] = -30.0 }, new JsonObject { ["ARTIFACTS"] = true }),
            Owner(15, "crt_screen", new JsonObject { ["Frequency"] = -30.0 }, new JsonObject { ["ARTIFACTS"] = false }),
            Owner(16, "crt_screen", new JsonObject { ["Frequency"] = -30.0 }, new JsonObject { ["ARTIFACTS"] = 1.0 }),
            Owner(17, "crt_screen", new JsonObject { ["Frequency"] = -30.0 }, new JsonObject { ["ARTIFACTS"] = "1" }),
            Owner(18, "crt_screen", new JsonObject { ["Frequency"] = -30.0 }, new JsonObject { ["ARTIFACTS"] = "on" }),
            Owner(19, "foliagesway", new JsonObject { ["speed"] = 5.0, ["strength"] = 0.52999997,
                ["directionweights"] = "0 0" }, new JsonObject { ["MODE"] = true }),
            Owner(20, "foliagesway", new JsonObject { ["speed"] = 5.0, ["strength"] = 0.52999997,
                ["directionweights"] = "0 0" }, new JsonObject { ["MODE"] = "vertex" })) };
        Write("scene.json", scene.ToJsonString());
        using var source = new ProjectSource(sourceDirectory);
        ShaderPeriodAnalysisResult analysis = ShaderPeriodAnalysis.Analyze(scene, source, null,
            [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20]);

        ShaderPeriodComponent grain = analysis.Components.Single(item => item.Patch.OwnerLayerId == 1);
        check(!grain.Component.AllowRetime && grain.Component.BasePeriod!.Evidence == CommonLoopPeriodEvidence.Analytic &&
            grain.Component.BasePeriod!.ExactSeconds == new CommonLoopRational(1) && grain.Component.BasePeriod!.Seconds == 1 &&
            grain.Patch.ConstantKey.Length == 0,
            "the real filmgrain frac(g_Time) clock is an exact one-second fixed period with no retimable constant");
        check(analysis.Unresolved.Single(item => item.OwnerLayerId == 2) is
                { Kind: ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism, Detail: "Shader source does not match a verified periodic equation." },
            "a second film-grain clock term leaves the verified frac(g_Time) family");

        ShaderPeriodComponent crt = analysis.Components.Single(item => item.Patch.OwnerLayerId == 3);
        check(crt.Component.AllowRetime && Math.Abs(crt.Component.BasePeriod!.Seconds - 2d / 30) < 1e-12 &&
            crt.Patch.ConstantKey == "Frequency" && crt.Patch.SpeedExponent == 1 && crt.Patch.OldValue == -30 &&
            crt.Evidence.Contains("2/abs(-30)", StringComparison.Ordinal),
            "the real crt_screen scan line and flicker share the period 2/abs(Frequency) through the Frequency constant");
        check(analysis.Components.All(item => item.Patch.OwnerLayerId != 4) &&
            analysis.Unresolved.All(item => item.OwnerLayerId != 4),
            "a disabled ARTIFACTS branch leaves crt_screen with no clock and no refusal");
        foreach (int enabledId in new[] { 14, 16, 17 })
        {
            ShaderPeriodComponent enabled = analysis.Components.Single(item => item.Patch.OwnerLayerId == enabledId);
            check(Math.Abs(enabled.Component.BasePeriod!.Seconds - 2d / 30) < 1e-12 &&
                enabled.Patch.ConstantKey == "Frequency" && analysis.Unresolved.All(item => item.OwnerLayerId != enabledId),
                $"a JSON true, 1.0 or \"1\" ARTIFACTS combo (layer {enabledId}) enables the same crt_screen clock as int 1");
        }
        check(analysis.Components.All(item => item.Patch.OwnerLayerId != 15) &&
            analysis.Unresolved.All(item => item.OwnerLayerId != 15),
            "a JSON false ARTIFACTS combo disables the crt_screen branch exactly like int 0");
        check(analysis.Unresolved.Single(item => item.OwnerLayerId == 18) is
                { Kind: ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism, Detail: var artifactsDetail } &&
            artifactsDetail.Contains("ARTIFACTS combo value is neither an integer nor a boolean", StringComparison.Ordinal) &&
            analysis.Components.All(item => item.Patch.OwnerLayerId != 18),
            "an unreadable ARTIFACTS combo refuses the pass instead of silently treating the clock branch as disabled");

        ShaderTemporalUnresolved shafts = analysis.Unresolved.Single(item => item.OwnerLayerId == 5);
        check(shafts.Kind == ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism &&
            shafts.Detail.Contains("0.0047111", StringComparison.Ordinal) &&
            shafts.Detail.Contains("1061.3 s", StringComparison.Ordinal) &&
            shafts.Detail.Contains("joint return inside the 600-second loop ceiling", StringComparison.Ordinal),
            "lightshafts states its four UV rates and the joint return that stays past the loop ceiling");
        // 文案复述调用方传入的实际上限（--loop-length-max），判定本身不随上限变化。
        ShaderTemporalUnresolved longShafts = ShaderPeriodAnalysis.Analyze(scene, source, null,
            [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20], loopCeilingSeconds: 3600)
            .Unresolved.Single(item => item.OwnerLayerId == 5);
        check(longShafts.Kind == shafts.Kind && longShafts.Detail.Contains("3600-second loop ceiling", StringComparison.Ordinal) &&
            !longShafts.Detail.Contains(" 600-second", StringComparison.Ordinal),
            "lightshafts quotes the loop ceiling the caller passed instead of a fixed number");

        ShaderTemporalUnresolved iris = analysis.Unresolved.Single(item => item.OwnerLayerId == 6);
        check(iris.Kind == ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism &&
            iris.Detail.Contains("floor(g_Time * g_Speed + phase)", StringComparison.Ordinal) &&
            iris.Detail.Contains("irrational", StringComparison.Ordinal),
            "iris names the integer step index and the irrational sine frequencies that prevent any period");

        ShaderTemporalUnresolved foliage = analysis.Unresolved.Single(item => item.OwnerLayerId == 7);
        check(foliage.Kind == ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism &&
            foliage.Detail.Contains("speeduv * (1, -0.16161616", StringComparison.Ordinal) &&
            foliage.Detail.Contains("0.000024801587", StringComparison.Ordinal),
            "foliage sway names the slowest of its eight sway terms and its out-of-range period");
        check(foliage.Detail.Contains("2*pi*443520/speeduv", StringComparison.Ordinal) &&
            foliage.Detail.Contains("557343.7 s", StringComparison.Ordinal),
            "foliage sway states the common base of all eight terms rather than an unsourced bound");
        check(analysis.Components.All(item => item.Patch.OwnerLayerId != 10) &&
            analysis.Unresolved.All(item => item.OwnerLayerId != 10),
            "a zero foliage-sway strength scales every sine to no motion instead of refusing the pass");
        check(analysis.Components.All(item => item.Patch.OwnerLayerId != 11) &&
            analysis.Unresolved.All(item => item.OwnerLayerId != 11),
            "a zero foliage-sway speeduv stops the clock instead of refusing the pass");
        check(analysis.Components.All(item => item.Patch.OwnerLayerId != 12) &&
            analysis.Unresolved.All(item => item.OwnerLayerId != 12),
            "zero foliage-sway directionweights gate both vertex axes instead of refusing the pass");
        check(analysis.Unresolved.Single(item => item.OwnerLayerId == 13) is
                { Kind: ShaderTemporalUnresolvedKind.NonPeriodicOrDriftingMechanism, Detail: var vertexDetail } &&
            vertexDetail.Contains("speed * (1, -0.16161616", StringComparison.Ordinal),
            "a swaying MODE 1 pass still refuses through its own 'speed' constant");

        check(analysis.Components.All(item => item.Patch.OwnerLayerId != 19) &&
            analysis.Unresolved.All(item => item.OwnerLayerId != 19),
            "a JSON true foliage-sway MODE reads as the vertex branch instead of throwing on GetValue<int>");
        check(analysis.Unresolved.Single(item => item.OwnerLayerId == 20) is
                { Kind: ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism, Detail: var foliageModeDetail } &&
            foliageModeDetail.Contains("MODE combo value is neither an integer nor a boolean", StringComparison.Ordinal),
            "an unreadable foliage-sway MODE refuses the pass instead of taking its zero-amplitude exemption");

        ShaderTemporalUnresolved shadow = analysis.Unresolved.Single(item => item.OwnerLayerId == 8);
        check(shadow.Kind == ShaderTemporalUnresolvedKind.UnsupportedShaderMechanism &&
            shadow.Detail.Contains("10000/1031 s", StringComparison.Ordinal) &&
            shadow.Detail.Contains("600000 frames", StringComparison.Ordinal),
            "the shadow_map hash reports its proven 10000/1031-second period and why that period is unusable");
        check(analysis.Components.All(item => item.Patch.OwnerLayerId != 9) &&
            analysis.Unresolved.All(item => item.OwnerLayerId != 9),
            "a zero static-noise amount removes the shadow_map clock instead of refusing the pass");

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
