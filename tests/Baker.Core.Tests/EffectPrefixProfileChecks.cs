using System.Reflection;
using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// fix/effect-prefix-profile：特效前缀路线的循环分析必须走 HybridScenePlanner.AnalyzeLoopForProfile，
/// 与整层路线、bake 前刷新同一口径。覆盖三档观感预算在前缀上确实生效、质量档双上限取优的记录，
/// 以及缓存记录里的档位溯源（preset / retime_budget_percent）与相位差记录（phase_drift_cycles）。
/// 场景取 SwayRetimeChecks 的同一组 stock foliagesway 摘录，前缀 = 颗粒 + 摆动两个效果。
/// </summary>
internal static class EffectPrefixProfileChecks
{
    private static readonly MethodInfo AnalyzePrefix = typeof(HybridLoopService).Assembly
        .GetType("Baker.Core.EffectPrefixPlanner")!
        .GetMethod("AnalyzeIndexedPrefix", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly MethodInfo Propose = typeof(HybridLoopService).Assembly
        .GetType("Baker.Core.EffectPrefixPlanner")!
        .GetMethod("Propose", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly MethodInfo PrepareCaptureSource = typeof(HybridLoopService).Assembly
        .GetType("Baker.Core.EffectPrefixBakeService")!
        .GetMethod("PrepareCaptureSourceAsync", BindingFlags.Static | BindingFlags.NonPublic)!;

    internal static void Run(Action<bool, string> check, string root)
    {
        string sourceDirectory = Path.Combine(root, "effect-prefix-profile-source");
        void Write(string relative, string text)
        {
            string path = Path.Combine(sourceDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        Write("effects/foliagesway/effect.json", """{"passes":[{"material":"materials/effects/foliagesway.json"}]}""");
        Write("materials/effects/foliagesway.json", """{"passes":[{"shader":"effects/foliagesway"}]}""");
        Write("shaders/effects/foliagesway.frag", SwayRetimeChecks.Fragment);
        Write("shaders/effects/foliagesway.vert", SwayRetimeChecks.Vertex);
        Write("effects/filmgrain.json", """{"passes":[{"material":"materials/filmgrain.json"}]}""");
        Write("materials/filmgrain.json", """{"passes":[{"shader":"filmgrain"}]}""");
        Write("shaders/filmgrain.frag", "void main() { gl_FragColor = vec4(1.0); }");
        Write("shaders/filmgrain.vert", """
            uniform float g_Time;
            uniform float g_NoiseScale; // {"material":"scale","label":"ui_editor_properties_scale","default":10,"range":[0.0, 20.0]}
            void main() {
            	float t = frac(g_Time);
            	v_TexCoord = a_TexCoord.xyxy;
            	v_TexCoordNoise.xy = (a_TexCoord.xy + t) * g_NoiseScale;
            	v_TexCoordNoise.zw = (a_TexCoord.xy - t * 2.5) * g_NoiseScale * 0.52;
            	v_TexCoordNoise *= vec4(aspect, 1.0, aspect, 1.0);
            }
            """);
        // 前缀的宿主图层要过 EligibleOwner：自有图片、单 pass genericimage3、一张非 _rt_ 纹理。
        Write("models/leaf.json", """{"material":"materials/leaf.json"}""");
        Write("materials/leaf.json", """{"passes":[{"shader":"genericimage3","textures":["textures/leaf"]}]}""");

        // 前缀 = 颗粒（1 s 周期，给通用求解器基础候选）+ 摆动（只能在候选的整数倍上闭合）。
        JsonObject Owner() => new()
        {
            ["id"] = 7, ["image"] = "models/leaf.json", ["size"] = "3840 2160", ["scale"] = "1.09437 1.09437 1",
            ["effects"] = new JsonArray(
                new JsonObject { ["file"] = "effects/filmgrain.json", ["id"] = 11,
                    ["passes"] = new JsonArray(new JsonObject { ["constantshadervalues"] = new JsonObject { ["scale"] = 10.0 } }) },
                new JsonObject { ["file"] = "effects/foliagesway/effect.json", ["id"] = 12,
                    ["passes"] = new JsonArray(new JsonObject { ["constantshadervalues"] = new JsonObject {
                        ["speeduv"] = 3.55, ["strength"] = 0.2, ["ratio"] = 0.3 } }) })
        };
        JsonObject Scene() => new() { ["objects"] = new JsonArray(Owner()) };
        Write("scene.json", Scene().ToJsonString());
        using var source = new ProjectSource(sourceDirectory);

        static HybridAnalyzeRequest Request(string? preset) =>
            new(2, "s", "a", "o", 1920, 1080, 60, 1, SwayRetime: true, Preset: preset);
        JsonObject Prefix(string? preset) => (JsonObject)AnalyzePrefix.Invoke(null,
            [Scene(), source, null, new JsonObject(), new JsonObject(), 7, 2, Request(preset), new JsonObject(), null])!;
        JsonArray Proposals(string? preset) => (JsonArray)Propose.Invoke(null,
            [Scene(), source, sourceDirectory, new JsonObject(), new JsonObject(), Request(preset), new JsonObject()])!;

        JsonObject efficiency = Prefix(RetimeProfile.Efficiency), balanced = Prefix(RetimeProfile.Balanced),
            quality = Prefix(RetimeProfile.Quality);
        static JsonObject Selected(JsonObject loop) => loop["candidates"]!.AsArray().OfType<JsonObject>().First();
        static JsonObject Retime(JsonObject loop) => Selected(loop)["sway_retime"]!.AsObject();
        static double Visible(JsonObject loop) => Retime(loop)["max_change_visible_percent"]!.GetValue<double>();
        static ulong Frames(JsonObject loop) => Selected(loop)["frames"]!.GetValue<ulong>();

        // The production prefix capture must apply the shader coefficients that made its
        // selected period valid. A shared shader used by a retained suffix stays authored in
        // the separately extracted candidate/reference; only the capture project is patched.
        string captureDirectory = Path.Combine(root, "effect-prefix-retime-capture");
        string candidateDirectory = Path.Combine(root, "effect-prefix-retime-candidate");
        string referenceDirectory = Path.Combine(root, "effect-prefix-retime-reference");
        source.ExtractAsync(candidateDirectory, CancellationToken.None).GetAwaiter().GetResult();
        source.ExtractAsync(referenceDirectory, CancellationToken.None).GetAwaiter().GetResult();
        JsonObject captureScene = Scene();
        JsonObject suffix = captureScene["objects"]![0]!["effects"]![1]!.DeepClone().AsObject();
        suffix["id"] = 13;
        captureScene["objects"]![0]!["effects"]!.AsArray().Add(suffix);
        string pristineScene = captureScene.ToJsonString();
        JsonArray patches = ((Task<JsonArray>)PrepareCaptureSource.Invoke(null,
            [captureDirectory, source, null, captureScene, new JsonObject(), balanced, CancellationToken.None])!).GetAwaiter().GetResult();
        string capturedFrag = File.ReadAllText(Path.Combine(captureDirectory, "shaders/effects/foliagesway.frag"));
        string capturedVert = File.ReadAllText(Path.Combine(captureDirectory, "shaders/effects/foliagesway.vert"));
        JsonObject speed = Retime(balanced)["shaders"]![0]!["speeds"]![0]!.AsObject();
        double[] expected = speed["coefficients_new"]!.AsArray().Select(value => value!.GetValue<double>()).ToArray();
        check(patches.Count == 2 && patches.OfType<JsonObject>().All(patch =>
                patch["statements_rewritten"]!.GetValue<int>() == 2 &&
                patch["sha256"]!.GetValue<string>() == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    File.ReadAllBytes(patch["path"]!.GetValue<string>())))) &&
            ShaderTextPatch.TryParseClockTerms(capturedFrag + "\n" + capturedVert, out ShaderTextPatch.ClockTerms captured) &&
            captured.Sines.Evaluate(speed["speed"]!.GetValue<double>()).Concat(captured.CoSines.Evaluate(speed["speed"]!.GetValue<double>()))
                .Zip(expected).All(pair => Math.Abs(pair.First - pair.Second) < 1e-13) &&
            captureScene.ToJsonString() == pristineScene &&
            new[] { sourceDirectory, candidateDirectory, referenceDirectory }.All(directory =>
                File.ReadAllText(Path.Combine(directory, "shaders/effects/foliagesway.frag")) == SwayRetimeChecks.Fragment &&
                File.ReadAllText(Path.Combine(directory, "shaders/effects/foliagesway.vert")) == SwayRetimeChecks.Vertex),
            "effect prefix capture: both sway shader stages match the selected period with auditable hashes, while the source, retained suffix and pristine reference remain unmodified");

        // 预算生效：前缀里的摆动项本来会留在未解析项里，走统一入口后按档位预算改频闭合，
        // 可见项改动不超过档位给的百分比；预算更宽的档在同一候选集上只会给出不更长的 L。
        check(new[] { efficiency, balanced, quality }.All(loop =>
                loop["unresolved"] is JsonArray { Count: 0 } && loop["candidates"] is JsonArray { Count: > 0 } &&
                loop["sway_retime"]!["status"]!.GetValue<string>() == "applied") &&
            Visible(efficiency) <= 5 + 1e-9 && Visible(balanced) <= 3 + 1e-9 &&
            Frames(efficiency) <= Frames(balanced),
            "effect prefix profile: every preset closes the prefix sway within its own visible budget and a wider budget never lengthens L");

        // 圈数下限与可见项第二闸不是档位旋钮，三档同一套；记录必须跟着候选写进前缀循环。
        check(new[] { efficiency, balanced, quality }.All(loop =>
                Retime(loop)["phase_drift_cycles"] is JsonValue &&
                Retime(loop)["max_visible_speed_deviation_pixels_per_second"] is JsonValue deviation &&
                deviation.GetValue<double>() <= SwayRecurrenceSolver.MaximumVisibleSpeedDeviationPixelsPerSecond + 1e-9 &&
                Frames(loop) >= (ulong)(SwayRecurrenceSolver.MinimumLoopSeconds * 60)),
            "effect prefix profile: the prefix candidate records phase drift and stays inside the shared cycle floor and visible speed gate");

        // 质量档双上限：档位上限比 600 s 长时与 600 s 各求一次并取可见改动更小者；
        // 其余档位只求一次，记录不出现。
        check(new[] { efficiency, balanced, quality }.All(loop => loop["quality_ceiling_used"] is null &&
            loop["maximum_seconds"]!.GetValue<double>() <= 600),
            "effect prefix profile: every preset defaults to at most 600 seconds and avoids duplicate ceiling solves");

        // 缓存记录带档位溯源：单看一条 effect_prefix_caches 就能知道这个前缀循环是哪一档、什么预算下求出来的。
        string?[] presets = [RetimeProfile.Efficiency, RetimeProfile.Balanced, RetimeProfile.Quality, null];
        JsonArray[] proposalLists = presets.Select(Proposals).ToArray();
        JsonObject[] profiles = proposalLists.Select(list => list.OfType<JsonObject>().First()).ToArray();

        // 回退候选：同一层的可闭合前缀由长到短全部提出，首选仍是最长的那个。调用方探测到终端落在共用缓冲上时
        // 才会用到短一级的那条，整层不因此退回实时。
        JsonObject[] balancedList = proposalLists[1].OfType<JsonObject>().ToArray();
        check(balancedList.Length == 2 &&
            balancedList[0]["prefix_effect_count"]!.GetValue<int>() == 2 && balancedList[0]["terminal_effect_id"]!.GetValue<int>() == 12 &&
            balancedList[1]["prefix_effect_count"]!.GetValue<int>() == 1 && balancedList[1]["terminal_effect_id"]!.GetValue<int>() == 11 &&
            balancedList.All(cache => cache["owner_layer_id"]!.GetValue<int>() == 7 && cache["retime_profile"] is JsonObject),
            "effect prefix profile: one owner proposes every closed prefix from longest to shortest so a rejected capture target falls back a level");
        JsonArray Controlled(string property, int caller = 99, string? hiddenPixelWrite = null) {
            var trace = new JsonObject { ["runtime_dependencies"] = new JsonArray(new JsonObject {
                ["owner"] = caller, ["target"] = 7, ["operation"] = "write", ["property"] = property, ["initialization"] = false }) };
            var controlledScene = Scene();
            controlledScene["objects"]!.AsArray().Add(new JsonObject { ["id"] = 99, ["visible"] = new JsonObject {
                ["script"] = "let target; export function init(){ target=thisScene.getLayer('leaf'); } export function update(value){ target.visible=engine.frametime>0; " + hiddenPixelWrite + " return value; }" } });
            var caches = (JsonArray)Propose.Invoke(null,
                [controlledScene, source, sourceDirectory, trace, new JsonObject(), Request(RetimeProfile.Balanced), new JsonObject()])!;
            check(trace["runtime_dependencies"]!.AsArray().Count == 1, "prefix analysis does not erase the original controller trace");
            return caches;
        }
        JsonArray visibilityCaches = Controlled("visible");
        check(visibilityCaches.Count == balancedList.Length && visibilityCaches.OfType<JsonObject>().All(cache =>
                cache["preserve_external_visibility"]?.GetValue<bool>() == true) &&
            Controlled("alpha").Count == 0 && Controlled("origin").Count == 0 && Controlled("visible", 7).Count == 0,
            "pure external visibility preserves periodic pixels, while pixel writes and owner scripts remain blocked, including late capture rechecks");
        check(Controlled("visible", hiddenPixelWrite: "if(engine.runtime>1000) target.alpha=0;").Count == 0 &&
            Controlled("visible", hiddenPixelWrite: "target['alpha']=0;").Count == 0,
            "a visible-only short trace cannot authorize unobserved pixel writes or computed controller members");
        check(profiles.All(cache => cache["prefix_effect_count"]!.GetValue<int>() == 2 &&
                cache["terminal_effect_id"]!.GetValue<int>() == 12 && cache["loop"] is JsonObject) &&
            profiles[0]["retime_profile"]!["preset"]!.GetValue<string>() == RetimeProfile.Efficiency &&
            profiles[0]["retime_profile"]!["retime_budget_percent"]!.GetValue<double>() == 5 &&
            profiles[1]["retime_profile"]!["preset"]!.GetValue<string>() == RetimeProfile.Balanced &&
            profiles[1]["retime_profile"]!["retime_budget_percent"]!.GetValue<double>() == 3 &&
            profiles[2]["retime_profile"]!["preset"]!.GetValue<string>() == RetimeProfile.Quality &&
            profiles[2]["retime_profile"]!["retime_budget_percent"] is null &&
            profiles[2]["loop"]!["quality_ceiling_used"] is null &&
            profiles[3]["retime_profile"]!["preset"] is null &&
            profiles[3]["retime_profile"]!["loop_max_seconds"]!.GetValue<double>() == RetimeProfile.DefaultLoopMaximumSeconds &&
            profiles[3]["loop"]!["quality_ceiling_used"] is null,
            "effect prefix profile: each proposed cache records the preset, its budget and its source, and no preset keeps the old unbudgeted behaviour");

        // fix-j：速度门限按输出短边换算，前缀路线与整层路线同一口径。4K 输出下缓存记录与前缀循环的 sway_retime 都写生效门限（×2），
        // 1080p 仍是 0.1 / 0.2。
        JsonObject fourK = ((JsonArray)Propose.Invoke(null, [Scene(), source, sourceDirectory, new JsonObject(), new JsonObject(),
            Request(RetimeProfile.Balanced) with { Width = 3840, Height = 2160 }, new JsonObject()])!).OfType<JsonObject>().First();
        static double Limit(JsonNode? record, string kind) => record![kind + "_speed_deviation_limit_pixels_per_second"]!.GetValue<double>();
        JsonNode? fourKRetime = fourK["loop"]!["candidates"]![0]!["sway_retime"];
        check(Limit(profiles[1]["retime_profile"], "slow") == 0.1 && Limit(profiles[1]["retime_profile"], "visible") == 0.2 &&
            Limit(fourK["retime_profile"], "slow") == 0.2 && Limit(fourK["retime_profile"], "visible") == 0.4 &&
            Limit(fourKRetime, "slow") == 0.2 && Limit(fourKRetime, "visible") == 0.4,
            "effect prefix profile: the cached profile and the prefix sway record carry the speed limits scaled by the output short edge");
    }
}
