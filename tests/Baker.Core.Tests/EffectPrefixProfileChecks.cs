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
        .GetMethod("AnalyzePrefix", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly MethodInfo Propose = typeof(HybridLoopService).Assembly
        .GetType("Baker.Core.EffectPrefixPlanner")!
        .GetMethod("Propose", BindingFlags.Static | BindingFlags.NonPublic)!;

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
            [Scene(), source, null, new JsonObject(), new JsonObject(), 7, 2, Request(preset), new JsonObject()])!;
        JsonArray Proposals(string? preset) => (JsonArray)Propose.Invoke(null,
            [Scene(), source, sourceDirectory, new JsonObject(), new JsonObject(), Request(preset), new JsonObject()])!;

        JsonObject efficiency = Prefix(RetimeProfile.Efficiency), balanced = Prefix(RetimeProfile.Balanced),
            quality = Prefix(RetimeProfile.Quality);
        static JsonObject Selected(JsonObject loop) => loop["candidates"]!.AsArray().OfType<JsonObject>().First();
        static JsonObject Retime(JsonObject loop) => Selected(loop)["sway_retime"]!.AsObject();
        static double Visible(JsonObject loop) => Retime(loop)["max_change_visible_percent"]!.GetValue<double>();
        static ulong Frames(JsonObject loop) => Selected(loop)["frames"]!.GetValue<ulong>();

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

        // 质量档双上限：档位上限 1200 s 被内嵌视频 2 GiB 在 1080p60 下收到 1175 s，与 600 s 各求一次并取可见改动更小者；
        // 其余档位只求一次，记录不出现。
        JsonObject used = quality["quality_ceiling_used"]!.AsObject();
        JsonObject[] tried = used["tried"]!.AsArray().OfType<JsonObject>().ToArray();
        static double Reading(JsonObject entry) => entry["max_change_visible_percent"]?.GetValue<double>() ?? double.PositiveInfinity;
        bool usePreset = used["source"]!.GetValue<string>() == "preset";
        check(tried.Length == 2 && tried[0]["source"]!.GetValue<string>() == "preset" &&
            tried[1]["source"]!.GetValue<string>() == "quality_comparison" &&
            Math.Abs(tried[0]["ceiling_seconds"]!.GetValue<double>() - 1175) < 1 &&
            tried[1]["ceiling_seconds"]!.GetValue<double>() == 600 &&
            used["seconds"]!.GetValue<double>() == quality["maximum_seconds"]!.GetValue<double>() &&
            Reading(tried[usePreset ? 0 : 1]) <= Reading(tried[usePreset ? 1 : 0]) &&
            efficiency["quality_ceiling_used"] is null && balanced["quality_ceiling_used"] is null,
            "effect prefix profile: the quality preset solves the prefix under both ceilings, keeps the smaller visible change and records both readings");

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
        check(profiles.All(cache => cache["prefix_effect_count"]!.GetValue<int>() == 2 &&
                cache["terminal_effect_id"]!.GetValue<int>() == 12 && cache["loop"] is JsonObject) &&
            profiles[0]["retime_profile"]!["preset"]!.GetValue<string>() == RetimeProfile.Efficiency &&
            profiles[0]["retime_profile"]!["retime_budget_percent"]!.GetValue<double>() == 5 &&
            profiles[1]["retime_profile"]!["preset"]!.GetValue<string>() == RetimeProfile.Balanced &&
            profiles[1]["retime_profile"]!["retime_budget_percent"]!.GetValue<double>() == 3 &&
            profiles[2]["retime_profile"]!["preset"]!.GetValue<string>() == RetimeProfile.Quality &&
            profiles[2]["retime_profile"]!["retime_budget_percent"] is null &&
            profiles[2]["loop"]!["quality_ceiling_used"] is JsonObject &&
            profiles[3]["retime_profile"]!["preset"] is null &&
            profiles[3]["retime_profile"]!["loop_max_seconds"]!.GetValue<double>() == RetimeProfile.DefaultLoopMaximumSeconds &&
            profiles[3]["loop"]!["quality_ceiling_used"] is null,
            "effect prefix profile: each proposed cache records the preset, its budget and its source, and no preset keeps the old unbudgeted behaviour");
    }
}
