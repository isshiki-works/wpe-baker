using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// 粒子"平稳随机、可淡化替换"判据 C1–C9、预热时长，以及无精灵轨道粒子层补记的未解析项。
/// <para>
/// 夹具是 48 案普查里的真实粒子定义（D:\WPE-particle-proto\survey，可用 WPE_PARTICLE_SURVEY 指定）：
/// 创意工坊摘录按仓库约定不进 tests/，所以和 tools.json 一样从本机读取，读不到就判失败，不静默跳过。
/// 每条检查都走公开的 HybridLoopService.Analyze，读 plan 里的 particle_stationarity，不反射判据内部类型。
/// </para>
/// </summary>
internal static class ParticleStationarityChecks
{
    private sealed record Fixture(JsonObject Object, JsonObject Definition, JsonObject Material);

    internal static string SurveyPath => Path.Combine(Environment.GetEnvironmentVariable("WPE_PARTICLE_SURVEY") is { Length: > 0 } configured
        ? configured : @"D:\WPE-particle-proto\survey", "survey.json");

    internal static void Run(Action<bool, string> check, string outputRoot)
    {
        string surveyPath = SurveyPath, surveyDirectory = Path.GetDirectoryName(surveyPath)!;
        check(File.Exists(surveyPath), "粒子判据夹具：找到了普查数据 survey.json（WPE_PARTICLE_SURVEY 或 D:\\WPE-particle-proto\\survey）");
        var survey = JsonNode.Parse(File.ReadAllText(surveyPath))!.AsArray().OfType<JsonObject>()
            .SelectMany(item => item["particles"]!.AsArray().OfType<JsonObject>()
                .Select(layer => (Case: item["id"]!.GetValue<string>(), Layer: layer)))
            .ToDictionary(pair => (pair.Case, pair.Layer["layer_id"]!.GetValue<int>()), pair => pair.Layer);

        // 对象取普查记下的场景对象字段；材质按普查记下的 shader / blending / combos / textures 重建 passes。
        Fixture Load(string caseId, int layerId)
        {
            JsonObject layer = survey[(caseId, layerId)];
            var obj = new JsonObject { ["id"] = layerId, ["name"] = layer["name"]!.DeepClone() };
            foreach (string key in new[] { "instanceoverride", "visible" })
                if (layer[key] is JsonNode value) obj[key] = value.DeepClone();
            JsonObject definition = JsonNode.Parse(File.ReadAllText(Path.Combine(surveyDirectory, caseId, $"particle-{layerId}.json")))!.AsObject();
            JsonArray Column(string key) => layer[key] as JsonArray ?? [];
            var passes = new JsonArray();
            for (int index = 0; index < Column("shader").Count; ++index)
            {
                var pass = new JsonObject { ["shader"] = Column("shader")[index]!.DeepClone() };
                if (Column("blending").ElementAtOrDefault(index) is JsonNode blending) pass["blending"] = blending.DeepClone();
                if (Column("combos").ElementAtOrDefault(index) is JsonObject combos) pass["combos"] = combos.DeepClone();
                if (Column("textures").ElementAtOrDefault(index) is JsonArray textures) pass["textures"] = textures.DeepClone();
                passes.Add(pass);
            }
            return new(obj, definition, new JsonObject { ["passes"] = passes });
        }

        int projectIndex = 0;
        // 把若干粒子层写成一个目录工程，跑一次 Analyze，返回整份报告。
        JsonObject Analyze(IReadOnlyList<Fixture> layers, JsonArray? dependencies = null, JsonArray? periods = null,
            JsonObject[]? extraObjects = null, bool withDependencies = true, uint fps = 60, uint fpsDenominator = 1,
            double? loopLengthMaximum = null, int[]? extraBaked = null)
        {
            string root = Path.Combine(outputRoot, "particle-stationarity", (++projectIndex).ToString("000"));
            Directory.CreateDirectory(Path.Combine(root, "particles"));
            Directory.CreateDirectory(Path.Combine(root, "materials"));
            File.WriteAllText(Path.Combine(root, "project.json"), "{\"type\":\"scene\",\"file\":\"scene.json\"}");
            var objects = new JsonArray();
            foreach (JsonObject extra in extraObjects ?? []) objects.Add(extra.DeepClone());
            foreach (Fixture layer in layers)
            {
                int id = layer.Object["id"]!.GetValue<int>();
                JsonObject definition = layer.Definition.DeepClone().AsObject();
                if (layer.Material["passes"] is JsonArray { Count: > 0 })
                {
                    definition["material"] = $"materials/particle-{id}.json";
                    File.WriteAllText(Path.Combine(root, "materials", $"particle-{id}.json"), layer.Material.ToJsonString());
                }
                else definition["material"] = $"materials/missing-{id}.json";
                File.WriteAllText(Path.Combine(root, "particles", $"particle-{id}.json"), definition.ToJsonString());
                JsonObject obj = layer.Object.DeepClone().AsObject();
                obj["particle"] = $"particles/particle-{id}.json";
                objects.Add(obj);
            }
            var scene = new JsonObject { ["objects"] = objects };
            File.WriteAllText(Path.Combine(root, "scene.json"), scene.ToJsonString());
            var runtime = new JsonObject { ["status"] = "complete", ["runtime_animation_periods"] = periods ?? new JsonArray(),
                ["runtime_layers"] = new JsonArray() };
            if (withDependencies) runtime["runtime_dependencies"] = dependencies ?? new JsonArray();
            using var source = new ProjectSource(root);
            return LoopAnalysis.Analyze(scene, source, null, runtime,
                [.. layers.Select(layer => layer.Object["id"]!.GetValue<int>()), .. extraBaked ?? []], fps, fpsDenominator,
                loopLengthMaximumSeconds: loopLengthMaximum).ToJson();
        }
        static JsonObject[] ParticleItems(JsonObject report, int owner) => report["unresolved"]!.AsArray().OfType<JsonObject>()
            .Where(item => item["owner_layer_id"]?.GetValue<int>() == owner && item["particle_stationarity"] is JsonObject).ToArray();
        JsonObject Verdict(Fixture layer, JsonArray? dependencies = null, bool withDependencies = true, JsonObject[]? extraObjects = null) =>
            ParticleItems(Analyze([layer], dependencies, extraObjects: extraObjects, withDependencies: withDependencies),
                layer.Object["id"]!.GetValue<int>()).Single()["particle_stationarity"]!.AsObject();
        static bool Stationary(JsonObject verdict) => verdict["stationary"]!.GetValue<bool>();
        static string[] Codes(JsonObject verdict) => verdict["failed_conditions"]!.AsArray()
            .Select(item => item!["condition"]!.GetValue<string>() + " " + item["code"]!.GetValue<string>()).Distinct().ToArray();
        static bool Only(JsonObject verdict, string code) => !Stationary(verdict) && Codes(verdict).SequenceEqual([code]);
        static bool NoCondition(JsonObject verdict, string condition) => Codes(verdict).All(code => !code.StartsWith(condition + " ", StringComparison.Ordinal));
        static double Seconds(JsonObject verdict, string key) => verdict[key]!.GetValue<double>();
        static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 1e-6;
        // 不满足条目携带的原始值快照（JSON 文本）。
        static string FailureValue(JsonObject verdict, string code) => verdict["failed_conditions"]!.AsArray().OfType<JsonObject>()
            .Single(item => item["code"]!.GetValue<string>() == code)["value"]!.GetValue<string>();
        static Fixture Mutate(Fixture layer, Action<JsonObject, JsonObject, JsonObject> change)
        {
            var copy = new Fixture(layer.Object.DeepClone().AsObject(), layer.Definition.DeepClone().AsObject(), layer.Material.DeepClone().AsObject());
            change(copy.Object, copy.Definition, copy.Material);
            return copy;
        }
        static JsonObject Node(JsonObject definition, string section, string name) =>
            definition[section]!.AsArray().OfType<JsonObject>().Single(node => node["name"]!.GetValue<string>() == name);

        Fixture droplets = Load("3666747189", 153), rain = Load("3666747189", 352), rainScreen = Load("3666747189", 130);
        // 湍流初始化器与封顶替换的真实定义：瑞鹤图烟雾（smoke1 预设，rate 覆盖 0.22）、时崎狂三樱花（leaves5 预设）、Silent Fields 鸟。
        Fixture smoke = Load("3441006668", 238), petals = Load("3565190341", 894), birds = Load("3661249043", 272);

        // ---- 正例 + 预热（instanceoverride.rate 是子系统的时间缩放：寿命与间歇上界都要除以它）----
        JsonObject dropletsVerdict = Verdict(droplets);
        check(Stationary(dropletsVerdict) && dropletsVerdict["failed_conditions"]!.AsArray().Count == 0,
            "Far From Home 水滴 153（sphererandom 常率、间歇发射延迟与时长都随机、alphafade 只给 fadeintime）通过 C1–C9");
        check(Near(Seconds(dropletsVerdict, "warmup_seconds"), 4 / 1.29) && Near(Seconds(dropletsVerdict, "lifetime_max_seconds"), 2 / 1.29),
            "水滴 153 预热 = starttime 0 + (寿命上界 1.0 s × 覆盖 2.0 + 间歇上界 (1 + 1) s) / rate 覆盖 1.29 = 3.100775 s；寿命上界 1.550388 s");
        check(Near(Seconds(dropletsVerdict, "emit_interval_seconds"), 1 / (11 * 0.55 * 1.29)) &&
            !dropletsVerdict["capped"]!.GetValue<bool>() && dropletsVerdict["warmup_generations"]!.GetValue<int>() == 1,
            "水滴 153 记录发射节拍 1 / (rate 11 × count 0.55 × rate 覆盖 1.29) = 0.128131 s；6.05 × 2 s = 12.1 < maxcount 32，不封顶，预热 1 代");
        JsonObject rainScreenVerdict = Verdict(rainScreen);
        check(Near(Seconds(rainScreenVerdict, "warmup_seconds"), 2 + 6 / 0.72),
            "预热把 starttime 算进去、寿命除以时间缩放：Gouttes de pluie 130 为 starttime 2 s + 寿命上界 6 s / rate 覆盖 0.72 = 10.333333 s（判据不通过也照算）");

        // ---- C2 封顶替换按周期锁定：Rain perspective 352 的 rate 400 × count 2.0 × 寿命 0.5 × 1.59 = 636 ≥ maxcount 512，寿命是确定值 ----
        static JsonObject Lock(JsonObject verdict) => verdict["cyclostationary_lock"]!.AsObject();
        static ulong Frames(JsonObject node, string key) => node[key]!.GetValue<ulong>();
        static JsonObject[] Candidates(JsonObject report) => [.. report["candidates"]!.AsArray().OfType<JsonObject>()];
        JsonObject rainReport = Analyze([rain]);
        JsonObject rainVerdict = ParticleItems(rainReport, 352).Single()["particle_stationarity"]!.AsObject();
        check(Stationary(rainVerdict) && rainVerdict["failed_conditions"]!.AsArray().Count == 0 && rainVerdict["capped"]!.GetValue<bool>() &&
            Frames(Lock(rainVerdict), "period_frames") == 48 && Near(Lock(rainVerdict)["period_seconds"]!.GetValue<double>(), 0.8) &&
            Frames(Lock(rainVerdict), "cycle_start_frame") == 39 && Lock(rainVerdict)["component"]!.GetValue<string>() == "particle_cycle/352/48" &&
            Lock(rainVerdict)["evidence"]!["duty"]!.GetValue<double>() == 0.805031 &&
            Near(Lock(rainVerdict)["evidence"]!["period_seconds_continuous"]!.GetValue<double>(), 0.795) &&
            Near(Seconds(rainVerdict, "warmup_seconds"), 0.65) && Near(Seconds(rainVerdict, "lifetime_max_seconds"), 0.795),
            "C2 锁定：Far From Home 雨透视 352 槽位常满、寿命确定，按渲染器逐帧推进（float 寿命 0.795 每帧减 1/60，第 48 帧 ≤ 0 被杀、同帧补发）替换周期是 48 帧 = 0.8 s（不是连续值 0.795 s），第 39 帧起计数状态严格 48 帧周期；预热换成 39 帧 = 0.65 s");
        check(Candidates(rainReport).Length > 0 && Candidates(rainReport).All(candidate => Frames(candidate, "frames") % 48 == 0) &&
            Frames(Candidates(rainReport)[0], "frames") == 48 &&
            Candidates(rainReport)[0]["components"]!.AsArray().OfType<JsonObject>().Any(component =>
                component["id"]?.GetValue<string>() == "particle_cycle/352/48" && component["cycles"]!.GetValue<ulong>() == 1) &&
            ParticleItems(rainReport, 352).Single()["detail"]!.GetValue<string>() == MessageCatalog.RenderLegacy("unresolved.particle_cyclostationary_locked", "48", "0.8"),
            "锁定周期交给求解器：只有雨透视 352 时候选全部是 48 帧的整数倍，候选分量里记 particle_cycle/352/48，未解析项文案说明按周期锁定（不再是 NoTemporalMechanism）");
        JsonObject rainAt30 = ParticleItems(Analyze([rain], fps: 30), 352).Single()["particle_stationarity"]!.AsObject();
        check(Stationary(rainAt30) && Frames(Lock(rainAt30), "period_frames") == 24 && Frames(Lock(rainAt30), "cycle_start_frame") == 20 &&
            Lock(rainAt30)["fps_num"]!.GetValue<uint>() == 30,
            "锁定周期随输出帧率按离散推进重算：同一层 30 fps 时是 24 帧（0.795 / (1/30) = 23.85 → 第 24 帧被杀），第 20 帧起进入周期态");
        JsonObject birdsVerdict = Verdict(birds);
        check(Stationary(birdsVerdict) && Frames(Lock(birdsVerdict), "period_frames") == 1501 &&
            Near(Lock(birdsVerdict)["period_seconds"]!.GetValue<double>(), 1501.0 / 60) && Frames(Lock(birdsVerdict), "cycle_start_frame") == 28 &&
            Lock(birdsVerdict)["evidence"]!["duty"]!.GetValue<double>() == 0.016 &&
            Near(Seconds(birdsVerdict, "warmup_seconds"), 0.466667),
            "C2 锁定：Silent Fields 鸟 272（rate 20 × 寿命 25 s ≫ maxcount 8）8 只鸟整批替换；float 25.0 每帧减 1/60 的累减误差让第 1500 帧还剩正数，周期是 1501 帧 = 25.016667 s；第 28 帧起严格周期，预热 0.466667 s");
        JsonObject clipObject = new() { ["id"] = 900, ["name"] = "clip" };
        JsonArray clipTrack = [new JsonObject { ["source_owner_layer_id"] = 900, ["mechanism"] = "animation", ["track_name"] = "clip",
            ["duration_seconds"] = 179.95, ["looping"] = true, ["playback_mode"] = "loop", ["event_driven"] = false, ["confidence"] = "high",
            ["playback_rate"] = 1.0 }];
        JsonObject withClip = Analyze([rain, droplets], periods: clipTrack, extraObjects: [clipObject], extraBaked: [900]);
        check(Candidates(withClip).Length > 0 && Candidates(withClip).All(candidate => Frames(candidate, "frames") % 10797 == 0),
            "锁定周期与轨道分量一起求解：179.95 s 的锁定轨道（10797 帧）与雨 48 帧的公共周期 172752 帧超过 180 s 上限时，另有不锁雨的候选（10797 帧）");
        JsonObject demoted = ParticleItems(withClip, 352).Single();
        check(Only(demoted["particle_stationarity"]!.AsObject(), "C2 lifetime_capped_no_common_loop") &&
            demoted["particle_stationarity"]!["cyclostationary_lock"] is null &&
            FailureValue(demoted["particle_stationarity"]!.AsObject(), "lifetime_capped_no_common_loop").Contains("\"period_frames\":48", StringComparison.Ordinal) &&
            FailureValue(demoted["particle_stationarity"]!.AsObject(), "lifetime_capped_no_common_loop").Contains("\"solver_fixed_frame_step\":172752", StringComparison.Ordinal) &&
            Near(Seconds(demoted["particle_stationarity"]!.AsObject(), "warmup_seconds"), 0.795) &&
            demoted["detail"]!.GetValue<string>() == MessageCatalog.RenderLegacy("unresolved.particle_not_stationary", "C2 lifetime_capped_no_common_loop") &&
            Stationary(ParticleItems(withClip, 153).Single()["particle_stationarity"]!.AsObject()) && withClip["no_candidate_reason"] is null,
            "锁定周期与其余分量在上限内没有公共循环、不锁时有：雨透视 352 退回拒绝（lifetime_capped_no_common_loop，快照记 48 帧与求解器步长 172752），预热恢复连续公式，文案改成不满足判据；平稳随机的水滴不受影响");

        // ---- 循环长度不是替换周期的整数倍时不放行（bake 选定循环后的防御）----
        JsonObject lockedResidualPlan = new()
        {
            ["settings"] = new JsonObject { ["width"] = 1920, ["height"] = 1080 }, ["canvas_width"] = 1920, ["canvas_height"] = 1080,
            ["layers"] = new JsonArray(new JsonObject { ["id"] = 352, ["name"] = "Rain perspective" }),
            ["video_groups"] = new JsonArray(new JsonObject { ["id"] = "group-1", ["layer_ids"] = new JsonArray(352), ["include_scene_clear"] = true }),
            ["loop"] = new JsonObject { ["unresolved"] = new JsonArray([.. ParticleItems(rainReport, 352).Select(item => (JsonNode)item.DeepClone())]) }
        };
        JsonObject lockedResidual = ResidualMasking.Classify(lockedResidualPlan,
            new JsonObject { ["objects"] = new JsonArray(new JsonObject { ["id"] = 352, ["particle"] = "particles/particle-352.json" }) }, _ => null);
        JsonObject rainResidual = lockedResidual["residual_layers"]!.AsArray().OfType<JsonObject>().Single();
        check(lockedResidual["status"]!.GetValue<string>() == "residual_maskable" && Frames(rainResidual["cyclostationary_lock"]!.AsObject(), "period_frames") == 48 &&
            rainResidual["proof"]!.GetValue<string>() == MessageCatalog.Get("residual.particle_cyclostationary_proof", MessageCatalog.Chinese, "48") &&
            ResidualMasking.WarmupFrames(lockedResidual, 60, 1) == 39 &&
            ResidualMasking.LockedCycleMismatch(lockedResidual, 48, 60, 1) is null && ResidualMasking.LockedCycleMismatch(lockedResidual, 1440, 120, 2) is null,
            "残差掩盖读锁定周期：雨透视 352 可掩盖，理由换成周期平稳的证明；预热 39 帧；循环 48 帧或 1440 帧（120/2 fps 与 60 fps 同速）都是 48 的整数倍，放行");
        JsonObject? offPhase = ResidualMasking.LockedCycleMismatch(lockedResidual, 600, 60, 1);
        JsonObject? otherRate = ResidualMasking.LockedCycleMismatch(lockedResidual, 48, 30, 1);
        check(offPhase is not null && offPhase["period_frames"]!.GetValue<ulong>() == 48 && offPhase["loop_frames"]!.GetValue<ulong>() == 600 &&
            offPhase["reason"]!.GetValue<string>() == MessageCatalog.Get("residual.particle_cycle_mismatch", MessageCatalog.Chinese, "352", 48UL, 600UL, "60", "60") &&
            otherRate is not null && ResidualMasking.LockedCycleMismatch(lockedResidual, 47, 60, 1) is not null,
            "循环长度不是替换周期的整数倍（600 帧 = 12.5 个周期、47 帧）或帧率与锁定时不同（30 fps）时不放行，理由写明周期帧数、循环帧数与帧率");

        // ---- T 算不准时维持拒绝 ----
        static bool Rejected(JsonObject verdict, string code) =>
            !Stationary(verdict) && Codes(verdict).Contains("C2 " + code) && verdict["cyclostationary_lock"] is null;
        check(Rejected(Verdict(Mutate(rain, (_, definition, _) => definition["maxcount"] = "512")), "lifetime_capped_parameters_unverified") &&
            Rejected(Verdict(Mutate(rain, (_, definition, _) => {
                JsonObject lifetimeNode = Node(definition, "initializer", "lifetimerandom");
                lifetimeNode["min"] = "0.5"; lifetimeNode["max"] = "0.5"; })), "lifetime_capped_parameters_unverified") &&
            Rejected(Verdict(Mutate(rain, (_, definition, _) => definition["emitter"]![0]!["flags"] = "2")), "lifetime_capped_parameters_unverified"),
            "C2 反例：maxcount、寿命或发射器 flags 写成字符串时渲染器读到的值说不清，替换周期算不准，维持拒绝（lifetime_capped_parameters_unverified）");
        // 合并 fix/loop-ceiling 后：上限是本次分析实际用的循环时长上限（--loop-max-seconds），不是写死的 180 s。
        Fixture birds200 = Mutate(birds, (_, definition, _) => {
            JsonObject lifetimeNode = Node(definition, "initializer", "lifetimerandom");
            lifetimeNode["min"] = 200; lifetimeNode["max"] = 200; });
        JsonObject birds200At180 = ParticleItems(Analyze([birds200], loopLengthMaximum: 180), 272).Single()["particle_stationarity"]!.AsObject();
        JsonObject birds200At600 = Verdict(birds200);
        check(Rejected(birds200At180, "lifetime_capped_period_exceeds_ceiling") &&
            FailureValue(birds200At180, "lifetime_capped_period_exceeds_ceiling").Contains("\"maximum_period_frames\":10800", StringComparison.Ordinal) &&
            Stationary(birds200At600) && Frames(Lock(birds200At600), "period_frames") == 12001,
            "C2：寿命 200 s 的替换周期 12001 帧按实际上限判——上限 180 s（10800 帧）时维持拒绝，缺省上限 600 s 时照旧锁定");
        check(Rejected(Verdict(Mutate(rain, (_, definition, _) => definition["emitter"]![0]!["flags"] = 2)), "lifetime_capped_cycle_not_reached"),
            "C2 反例：发射器 one_per_frame（flags 2）每帧至多补 1 个，计时器按 fmod 走余数，16 个周期内计数状态不回到同一值，维持拒绝（lifetime_capped_cycle_not_reached）");
        check(Only(Verdict(Mutate(rain, (_, definition, _) => definition.Remove("maxcount"))), "C2 maxcount_default_unverified") &&
            Codes(Verdict(Mutate(rain, (obj, _, _) => obj["instanceoverride"]!["rate"] = 0.0))).Contains("C1 rate_override_zero") &&
            Verdict(Mutate(rain, (obj, _, _) => obj["instanceoverride"]!["rate"] = 0.0))["cyclostationary_lock"] is null,
            "C2 反例：maxcount 缺省（引擎缺省未核）、rate 覆盖为 0（子系统不推进）时不锁定，维持 0ffe9c3 的拒绝");
        JsonObject birdsDisabledOverride = Verdict(Mutate(birds, (obj, definition, _) => { obj["instanceoverride"]!["lifetime"] = 0.5; definition["flags"] = 32; }));
        check(Stationary(birdsDisabledOverride) && Frames(Lock(birdsDisabledOverride), "period_frames") == 1501,
            "粒子顶层 flags 32（disable_lifetime_override）让 lifetime 覆盖失效：鸟加 lifetime 覆盖 0.5 后替换周期仍是 1501 帧");
        JsonObject birdsRandom = Verdict(Mutate(birds, (_, definition, _) => Node(definition, "initializer", "lifetimerandom")["min"] = 20));
        check(Stationary(birdsRandom) && birdsRandom["capped"]!.GetValue<bool>() && birdsRandom["warmup_generations"]!.GetValue<int>() == 6 &&
            Near(Seconds(birdsRandom, "warmup_seconds"), 150) && Near(Seconds(birdsRandom, "lifetime_max_seconds"), 25),
            "C2 正例：同一层寿命改成 20–25 s 随机后替换时刻逐代打散，放行；预热 = 寿命上界 25 s × (⌈25 / 5⌉ + 1 = 6 代) = 150 s");
        check(Only(Verdict(Mutate(droplets, (_, definition, _) => definition.Remove("maxcount"))), "C2 maxcount_default_unverified"),
            "C2 反例：没写 maxcount 时封顶与否要靠未核的缺省值，只因这一条不放行");

        // ---- C2 湍流初始化器：出生方向来自跨粒子共享、沿 CurlNoise 流线推进的采样点，不是每粒子独立标记 ----
        JsonObject smokeVerdict = Verdict(smoke);
        check(Only(smokeVerdict, "C2 turbulent_velocity_shared_field") &&
            FailureValue(smokeVerdict, "turbulent_velocity_shared_field").Contains("\"scale\":0.1", StringComparison.Ordinal) &&
            FailureValue(smokeVerdict, "turbulent_velocity_shared_field").Contains("\"timescale\":1", StringComparison.Ordinal),
            "C2 反例：瑞鹤图烟雾 238 的 turbulentvelocityrandom（scale 0.1、speed 250–260、timescale 缺省 1）共享风向，只因这一条不放行，快照记参数");
        check(Near(Seconds(smokeVerdict, "warmup_seconds"), 1 + 4 / 0.22) && Near(Seconds(smokeVerdict, "lifetime_max_seconds"), 4 / 0.22),
            "烟雾 238 的 rate 覆盖 0.22 让子系统慢放：寿命上界 4 s / 0.22 = 18.181818 s，预热 = starttime 1 + 18.181818 = 19.181818 s（旧口径 5 s 不够）");
        check(Stationary(Verdict(Mutate(smoke, (_, definition, _) => Node(definition, "initializer", "turbulentvelocityrandom")["scale"] = 0))),
            "C2 正例：同一层 scale 改 0（角度增益 max(0, scale × 0.5) = 0）后噪声不进入出生方向，方向恒为 forward 转 offset，放行");
        check(Stationary(Verdict(Mutate(smoke, (_, definition, _) => Node(definition, "initializer", "turbulentvelocityrandom")["timescale"] = 0))),
            "C2 正例：同一层 timescale 改 0 后共享采样点不推进，出生方向只由每粒子随机 phase 决定，放行");
        check(Stationary(Verdict(Mutate(smoke, (_, definition, _) => {
                JsonObject turbulent = Node(definition, "initializer", "turbulentvelocityrandom");
                turbulent["speedmin"] = 0; turbulent["speedmax"] = 0; }))),
            "C2 正例：同一层 speedmin = speedmax = 0 后湍流分量对速度没有贡献，放行");
        check(Only(Verdict(Mutate(smoke, (_, definition, _) => Node(definition, "initializer", "turbulentvelocityrandom")["scale"] = "a b")),
                "C2 turbulent_velocity_unreadable"),
            "C2 反例：湍流参数读不成单个常数时说不清，不放行");
        JsonObject petalsVerdict = Verdict(petals);
        check(Only(petalsVerdict, "C2 turbulent_velocity_shared_field") && Near(Seconds(petalsVerdict, "warmup_seconds"), 13),
            "C2 反例：时崎狂三樱花 894（leaves5：scale 0.5、speed 35–100、offset 3）同样只因共享风向不放行；预热 starttime 3 + 寿命 10 = 13 s 照算");

        // ---- C3 湍流算子：随子系统时间沿 x 平移的共享确定性场（Perlin 表周期 256）----
        Fixture withTurbulence = Mutate(droplets, (_, definition, _) => definition["operator"]!.AsArray().Add(new JsonObject { ["name"] = "turbulence" }));
        JsonObject turbulenceVerdict = Verdict(withTurbulence);
        check(Only(turbulenceVerdict, "C3 turbulence_shared_field") &&
            FailureValue(turbulenceVerdict, "turbulence_shared_field").Contains("\"field_period_system_seconds\":640", StringComparison.Ordinal),
            "C3 反例：加一个缺省参数的 turbulence 算子（timescale 20、scale 0.01、speed 500–1000）后不放行，快照记场周期 256 / (2 × 0.01 × 20) = 640 系统秒");
        check(Stationary(Verdict(Mutate(withTurbulence, (_, definition, _) => Node(definition, "operator", "turbulence")["timescale"] = 0))),
            "C3 正例：turbulence 的 timescale 为 0 时场静止，每个粒子的轨迹只是自身标记的函数，放行");
        check(Stationary(Verdict(Mutate(withTurbulence, (_, definition, _) => {
                JsonObject turbulence = Node(definition, "operator", "turbulence");
                turbulence["speedmin"] = 0; turbulence["speedmax"] = 0; }))),
            "C3 正例：turbulence 的速度区间为 0 时算子不起作用，放行");
        check(Stationary(Verdict(Mutate(withTurbulence, (_, definition, _) => Node(definition, "operator", "turbulence")["mask"] = "0 0 0"))),
            "C3 正例：turbulence 的 mask 全 0 时算子不起作用，放行");

        // ---- C1 发射器 ----
        check(Codes(Verdict(Load("3151551777", 799))).Contains("C1 emitter_burst"),
            "C1 反例：3151551777 层 799 的 instantaneous 20 是爆发发射，不放行");
        var defaultRate = Load("3516174947", 171);
        var explicitRate = Mutate(defaultRate, (_, definition, _) => {
            foreach (JsonObject emitter in definition["emitter"]!.AsArray().OfType<JsonObject>())
                emitter["rate"] ??= 5.0;
        });
        check(Stationary(Verdict(defaultRate)) && JsonNode.DeepEquals(Verdict(defaultRate), Verdict(explicitRate)),
            "C1：省略 rate 与原生显式默认 5 的判定、封顶和预热结果相同");
        check(Codes(Verdict(Load("3151551777", 37))).Contains("C1 emitter_audio_input"),
            "C1 反例：audioprocessingmode 非零的发射器是外部输入，不放行");
        check(Only(Verdict(Mutate(droplets, (_, definition, _) => {
                JsonObject emitter = definition["emitter"]![0]!.AsObject();
                emitter["minperiodicdelay"] = 1; emitter["minperiodicduration"] = 1; })), "C1 emitter_periodic_deterministic"),
            "C1 反例：间歇发射四个值退化成确定值（delay 1/1、duration 1/1）是解析周期，不当随机放行");
        check(Only(Verdict(Mutate(droplets, (_, definition, _) => definition["emitter"]![0]!.AsObject().Remove("minperiodicdelay"))),
                "C1 emitter_periodic_incomplete"),
            "C1 反例：间歇发射字段缺一个，要靠未核缺省值，不放行");
        check(Only(Verdict(Mutate(droplets, (_, definition, _) => definition["emitter"]![0]!["duration"] = 5)), "C1 emitter_finite_duration"),
            "C1 反例：发射器 duration 非零（只发一段时间）不是平稳发射");
        // 场景对象不落盘、直接进 Analyze，数值要用 double 字面量（CLR int 的 JsonValue 读不成 double）。
        check(Codes(Verdict(Mutate(droplets, (obj, _, _) => obj["instanceoverride"]!["rate"] = 0.0))).Contains("C1 rate_override_zero"),
            "C1 反例：instanceoverride.rate 为 0 时子系统时间不推进，不放行");

        // ---- C2 初始化器与寿命 ----
        check(NoCondition(dropletsVerdict, "C2"), "C2 正例：lifetimerandom/sizerandom 等每粒子独立抽取的初始化器在允许集合里且寿命有界");
        check(Only(Verdict(Mutate(droplets, (_, definition, _) => {
                JsonArray initializers = definition["initializer"]!.AsArray();
                initializers.Remove(Node(definition, "initializer", "lifetimerandom")); })),
                "C2 lifetime_default_unverified"),
            "C2 反例：没有 lifetimerandom 时寿命用引擎缺省值，缺省值未核，不放行");
        check(Only(Verdict(Mutate(droplets, (_, definition, _) => definition["initializer"]!.AsArray()
                .Add(new JsonObject { ["name"] = "positionrandom" }))), "C2 initializer_kind"),
            "C2 反例：允许集合之外的初始化器不放行");

        // ---- C3 算子 ----
        JsonObject stars = Verdict(Load("3363252053", 200));
        check(Codes(stars).Contains("C3 oscillate_phase_basis_unverified"),
            "C3 反例：Stars 200 的 oscillatealpha 只有 frequencymax，频率与相位都退化，相位基准未核，不放行");
        check(NoCondition(Verdict(Mutate(Load("3363252053", 200), (_, definition, _) =>
                Node(definition, "operator", "oscillatealpha")["frequencymin"] = 0.5)), "C3"),
            "C3 正例：同一个 oscillatealpha 补上 frequencymin ≠ frequencymax 后每粒子随机频率，C3 放行");
        check(NoCondition(Verdict(Load("3151551777", 704)), "C3"),
            "C3 正例：Snow storm 704 的 oscillateposition 频率与相位都有随机区间");
        JsonObject drippingWater = Verdict(Load("3670813883", 431));
        check(Codes(drippingWater).Contains("C3 operator_kind"), "C3 反例：boids 有群体耦合状态，不放行");

        // ---- C4 控制点 ----
        check(NoCondition(rainVerdict, "C4"), "C4 正例：flags 全为 0 的控制点放行");
        JsonObject trails = Verdict(Load("3363252053", 560));
        check(Codes(trails).Contains("C4 controlpoint_follows_cursor"), "C4 反例：Trails 2 560 的控制点 flags 1 跟随鼠标，不放行");
        check(Codes(Verdict(Load("3479521040", 718))).Contains("C4 controlpoint_flags_unverified") &&
            Codes(drippingWater).Contains("C4 controlpoint_flags_unverified"),
            "C4 反例：flags 2（Bird 718）与 flags 16（3670813883 层 431）位含义未核，同样不放行");

        // ---- C5 子系统 ----
        check(NoCondition(rainVerdict, "C5") && NoCondition(dropletsVerdict, "C5"), "C5 正例：children 缺省或为 null 放行");
        check(Codes(rainScreenVerdict).Contains("C5 child_systems_not_recursed"), "C5 反例：Gouttes de pluie 4k 130 带子系统，v1 不递归，不放行");

        // ---- C6 材质 ----
        check(NoCondition(rainVerdict, "C6"), "C6 正例：genericparticle 且不带 REFRACT 组合");
        check(Only(Verdict(Load("3666747189", 288)), "C6 shader_reads_framebuffer"),
            "C6 反例：Rain1 288 着色器名是 genericparticle，但 REFRACT 组合为 1 会采样帧缓冲，只因这一条不放行");
        check(Only(Verdict(Mutate(droplets, (_, _, material) => material["passes"]![0]!["shader"] = "genericparticlerefract")), "C6 shader_reads_framebuffer"),
            "C6 反例：着色器名含 refract 不放行");
        check(Only(Verdict(Mutate(droplets, (_, _, material) => material["passes"] = new JsonArray())), "C6 material_unreadable"),
            "C6 反例：材质读不到就判不满足");

        // ---- C7 脚本 ----
        check(NoCondition(rainVerdict, "C7"), "C7 正例：运行时观测里没有针对该层的脚本依赖");
        check(Only(Verdict(droplets, [new JsonObject { ["owner"] = 5, ["target"] = 153, ["operation"] = "write", ["property"] = "alpha",
                ["initialization"] = false }]), "C7 script_writes_object"),
            "C7 反例：别的层脚本逐帧写这个粒子层的属性，不放行");
        check(Stationary(Verdict(droplets, [new JsonObject { ["owner"] = 5, ["target"] = 153, ["operation"] = "write", ["property"] = "alpha",
                ["initialization"] = true }])),
            "C7 正例：只在初始化时写一次的依赖不算脚本驱动");
        check(Only(Verdict(Mutate(droplets, (obj, _, _) => obj["parent"] = 900),
                [new JsonObject { ["owner"] = 900, ["target"] = -1, ["operation"] = "time", ["property"] = "frametime", ["initialization"] = false }],
                extraObjects: [new JsonObject { ["id"] = 900, ["name"] = "group" }]), "C7 script_drives_object"),
            "C7 反例：祖先对象上逐帧运行的脚本等于移动发射器，不放行");
        check(Only(Verdict(droplets, withDependencies: false), "C7 script_evidence_unavailable"),
            "C7 反例：运行时观测缺 runtime_dependencies 时证明不了没有脚本");

        // ---- C8 覆盖与属性绑定 ----
        check(NoCondition(dropletsVerdict, "C8"), "C8 正例：数值覆盖与 {user, value} 可见性绑定是常数");
        JsonObject blinking = Verdict(Load("3151551777", 805));
        check(Codes(blinking).Contains("C8 override_not_constant") && Codes(blinking).Contains("C8 property_animated"),
            "C8 反例：Blinking Stars 805 的 instanceoverride.alpha 与 visible 都挂着脚本，不放行");

        // ---- C9 渲染器与精灵帧 ----
        check(NoCondition(rainVerdict, "C9") && NoCondition(dropletsVerdict, "C9"), "C9 正例：spritetrail + randomframe 放行");
        check(Codes(trails).Contains("C9 renderer_kind"), "C9 反例：rope 渲染器不放行");
        var defaultRenderer = Mutate(Load("3151551777", 793), (obj, _, _) => obj.Remove("visible"));
        var explicitRenderer = Mutate(defaultRenderer, (_, definition, _) =>
            definition["renderer"] = new JsonArray(new JsonObject { ["name"] = "sprite" }));
        check(Stationary(Verdict(defaultRenderer)) && JsonNode.DeepEquals(Verdict(defaultRenderer), Verdict(explicitRenderer)),
            "C9：省略 renderer 与原生默认 sprite 的判定相同");
        check(Codes(Verdict(Mutate(defaultRenderer, (_, definition, _) => definition["renderer"] = "sprite")))
            .Contains("C9 renderer_definition_invalid"), "C9：畸形 renderer 字段不当作缺省值");
        check(Only(Verdict(Mutate(droplets, (_, definition, _) => definition["animationmode"] = "once")), "C9 animation_mode_unverified"),
            "C9 反例：未核过的 animationmode 写法不放行");

        // ---- 未解析项的产生 ----
        JsonObject mixed = Analyze([droplets, Load("3363252053", 560)]);
        JsonObject[] dropletItems = ParticleItems(mixed, 153), trailItems = ParticleItems(mixed, 560);
        check(dropletItems.Length == 1 && dropletItems[0]["kind"]!.GetValue<string>() == "runtime_animation" &&
            dropletItems[0]["mechanism"]!.GetValue<string>() == "particle_system" && Stationary(dropletItems[0]["particle_stationarity"]!.AsObject()) &&
            dropletItems[0]["detail"]!.GetValue<string>() == MessageCatalog.RenderLegacy("unresolved.particle_stationary_random") &&
            !mixed.ToJsonString().Contains("detail_localized", StringComparison.Ordinal),
            "没有精灵轨道的粒子层也产生一条 particle_system 未解析项：通过判据的标 stationary=true，文案走 MessageCatalog 双语");
        check(trailItems.Length == 1 && !Stationary(trailItems[0]["particle_stationarity"]!.AsObject()) &&
            trailItems[0]["detail"]!.GetValue<string>().Contains("C4 controlpoint_follows_cursor", StringComparison.Ordinal),
            "不通过判据的无精灵轨道粒子层同样记一条，文案点出不满足的条件代号");
        check(mixed["unresolved"]!.AsArray().Count == 2 && mixed["candidates"]!.AsArray().Count == 0 && !mixed["source_static"]!.GetValue<bool>(),
            "只有粒子层时不再走静态证明，未解析项恰好每层一条");

        // ---- 残差掩盖读判据字段（真实定义）：Far From Home 水滴 153 可掩盖、预热 3.100775 s；雨透视 352（封顶确定寿命）按周期锁定后可掩盖；
        // Gouttes 4k 130（子系统）与 Trails 560（跟鼠标）不可掩盖，理由带条件代号。整份未解析项直接来自 Analyze。----
        JsonObject residualReport = Analyze([droplets, rain, rainScreen, Load("3363252053", 560)]);
        JsonObject ResidualPlan(JsonArray unresolved, params int[][] groups) => new()
        {
            ["settings"] = new JsonObject { ["width"] = 1920, ["height"] = 1080 }, ["canvas_width"] = 1920, ["canvas_height"] = 1080,
            ["layers"] = new JsonArray([.. new[] { 65, 153, 352, 130, 560 }.Select(id => (JsonNode)new JsonObject { ["id"] = id, ["name"] = $"L{id}" })]),
            ["video_groups"] = new JsonArray([.. groups.Select((layers, index) => (JsonNode)new JsonObject {
                ["id"] = $"group-{index + 1}", ["layer_ids"] = new JsonArray([.. layers.Select(id => (JsonNode)JsonValue.Create(id))]),
                ["include_scene_clear"] = index == 0 })]),
            ["loop"] = new JsonObject { ["unresolved"] = unresolved }
        };
        var residualScene = new JsonObject { ["objects"] = new JsonArray([.. new[] { 153, 352, 130, 560 }.Select(id =>
            (JsonNode)new JsonObject { ["id"] = id, ["particle"] = $"particles/particle-{id}.json" })]) };
        JsonObject mixedResidual = ResidualMasking.Classify(ResidualPlan(residualReport["unresolved"]!.DeepClone().AsArray(), [65], [153, 352], [130, 560]),
            residualScene, _ => null);
        JsonObject[] blockedParticles = mixedResidual["blocking_components"]!.AsArray().OfType<JsonObject>().ToArray();
        check(mixedResidual["status"]!.GetValue<string>() == "rejected" &&
            ResidualMasking.ResidualOwners(mixedResidual).SequenceEqual([153, 352]) &&
            blockedParticles.Select(item => item["owner_layer_id"]!.GetValue<int>()).Order().SequenceEqual([130, 560]) &&
            residualReport["candidates"]!.AsArray().OfType<JsonObject>().All(candidate => candidate["frames"]!.GetValue<ulong>() % 48 == 0) &&
            blockedParticles.Single(item => item["owner_layer_id"]!.GetValue<int>() == 130)["reason"]!.GetValue<string>().Contains("C5 child_systems_not_recursed", StringComparison.Ordinal) &&
            blockedParticles.Single(item => item["owner_layer_id"]!.GetValue<int>() == 560)["reason"]!.GetValue<string>().Contains("C4 controlpoint_follows_cursor", StringComparison.Ordinal),
            "残差掩盖读真实粒子定义的判据结论：水滴 153 与锁定周期的雨透视 352 可掩盖（候选全是 48 帧的倍数）；带子系统的 130 与跟鼠标的 560 不可掩盖，理由列出条件代号");
        JsonArray stationaryItems = new([.. residualReport["unresolved"]!.AsArray().OfType<JsonObject>()
            .Where(item => item["owner_layer_id"]!.GetValue<int>() is 153).Select(item => (JsonNode)item.DeepClone())]);
        JsonObject dropletPlan = ResidualPlan(stationaryItems, [65], [153, 352]);
        JsonObject dropletResidual = ResidualMasking.Classify(dropletPlan, residualScene, _ => null);
        JsonObject gatePlan = dropletPlan.DeepClone().AsObject();
        gatePlan["route"] = "whole_layer";
        gatePlan["blockers"] = new JsonArray();
        gatePlan["whole_layer"] = new JsonObject { ["blockers"] = new JsonArray(), ["status"] = "unavailable" };
        gatePlan["loop"]!["candidates"] = new JsonArray(new JsonObject { ["frames"] = 1370 });
        check(dropletResidual["status"]!.GetValue<string>() == "residual_maskable" &&
            Near(dropletResidual["max_warmup_seconds"]!.GetValue<double>(), 4 / 1.29) && ResidualMasking.WarmupFrames(dropletResidual, 60, 1) == 187 &&
            ResidualMasking.ResidualGroupIndexes(dropletPlan, dropletResidual).SequenceEqual([1]) &&
            ResidualMasking.LayoutAllowsMasking(dropletPlan, dropletResidual) &&
            Admission.ApplyResidualLayoutGate(gatePlan, residualScene, _ => null) is null && gatePlan["blockers"]!.AsArray().Count == 0,
            "只剩平稳随机的水滴时可掩盖：预热 3.100775 s = 187 帧，残差组是它所在的透明组，布局门放行");

        JsonArray spriteTrack = [new JsonObject { ["source_owner_layer_id"] = 153, ["mechanism"] = "sprite", ["track_name"] = "particle/drop",
            ["duration_seconds"] = 1.0, ["looping"] = true, ["playback_mode"] = "loop", ["event_driven"] = false, ["confidence"] = "high" }];
        JsonObject sprite = Analyze([droplets], periods: spriteTrack);
        JsonObject[] spriteItems = ParticleItems(sprite, 153);
        check(spriteItems.Length == 1 && spriteItems[0]["particle_nonperiodic_reason"]!.GetValue<string>() == "particle_nonperiodic_random_frame" &&
            spriteItems[0]["mechanism"] is null && Stationary(spriteItems[0]["particle_stationarity"]!.AsObject()) &&
            sprite["unresolved"]!.AsArray().Count == 1,
            "有精灵轨道的粒子未解析项保留原理由与文案，只附 particle_stationarity，不再重复补一条 particle_system");

        // ---- 只有平稳随机粒子、没有任何周期分量：循环长度取 min(60 秒, 上限)，接缝交叉淡化 ----
        static ulong[] CandidateFrames(JsonObject report) => report["candidates"]!.AsArray().Select(item => item!["frames"]!.GetValue<ulong>()).ToArray();
        // 合并 fix/particle-turbulence 后雨透视 352 是锁定周期的分量（48 帧），有分量就由求解器定长度，
        // 所以默认长度只在"只有平稳随机粒子、没有任何分量"时成立，这里用水滴 153 单层。
        JsonObject onlyStationary = Analyze([droplets]);
        JsonObject particleDefault = onlyStationary["loop_length_default"]!.AsObject();
        JsonObject defaultCandidate = onlyStationary["candidates"]!.AsArray().Single()!.AsObject();
        check(defaultCandidate["frames"]!.GetValue<ulong>() == 3600 && defaultCandidate["seconds"]!.GetValue<double>() == 60 &&
            defaultCandidate["components"]!.AsArray().Count == 0 && defaultCandidate["patches"]!.AsArray().Count == 0 &&
            defaultCandidate["loop_length_source"]!.GetValue<string>() == "stationary_particle_default" &&
            particleDefault["status"]!.GetValue<string>() == "applied" && particleDefault["kind"]!.GetValue<string>() == "stationary_particle_default" &&
            particleDefault["particle_layer_ids"]!.AsArray().Select(id => id!.GetValue<int>()).SequenceEqual([153]) &&
            Near(particleDefault["max_particle_lifetime_seconds"]!.GetValue<double>(), 2 / 1.29) &&
            particleDefault["loop_length_maximum_seconds"]!.GetValue<double>() == 600 &&
            particleDefault["summary_zh"]!.GetValue<string>() == "粒子系统无周期：循环长度取默认值 60 s，接缝处交叉淡化。" &&
            onlyStationary["unresolved"]!.AsArray().Count == 1 && onlyStationary["no_candidate_reason"] is null &&
            onlyStationary["status"]!.GetValue<string>() == "analytic_candidate_requires_seam_validation",
            "只有平稳随机的水滴、没有周期分量：追加一个 60 秒（3600 帧）的默认候选并记下来源，粒子未解析项原样留给残差掩盖");
        JsonObject narrated = PlanNarrative.Summarize(new JsonObject { ["blockers"] = new JsonArray(), ["loop"] = onlyStationary.DeepClone(),
            ["settings"] = new JsonObject { ["fps_numerator"] = 60, ["fps_denominator"] = 1 } });
        check(narrated["verdict"]!.GetValue<string>() == PlanNarrative.Bakeable &&
            narrated["zh"]!.GetValue<string>().EndsWith("粒子系统无周期：循环长度取默认值 60 s，接缝处交叉淡化。", StringComparison.Ordinal) &&
            narrated["en"]!.GetValue<string>().EndsWith("Particle systems have no period: loop length defaults to 60 s, seam crossfaded.", StringComparison.Ordinal),
            "结论行中英文都说明粒子没有周期、按 60 秒循环并在接缝处交叉淡化");
        JsonObject shortCeiling = Analyze([droplets], loopLengthMaximum: 45.5);
        JsonObject ntsc = Analyze([droplets], fps: 60000, fpsDenominator: 1001);
        check(CandidateFrames(shortCeiling).SequenceEqual([2730UL]) && shortCeiling["loop_length_default"]!["seconds"]!.GetValue<double>() == 45.5 &&
            CandidateFrames(ntsc).SequenceEqual([3596UL]) && ntsc["loop_length_default"]!["seconds"]!.GetValue<double>() <= 60 &&
            CandidateFrames(sprite).SequenceEqual([3600UL]),
            "默认长度受 --loop-max-seconds 约束（45.5 秒上限取 2730 帧），向下取整到输出帧网格（59.94 fps 取 3596 帧），带精灵轨道的平稳粒子同样适用");

        var longParticle = Mutate(droplets, (_, definition, _) =>
            definition["initializer"]!.AsArray().OfType<JsonObject>().Single(item => item["name"]!.GetValue<string>() == "lifetimerandom")["max"] = 40);
        JsonObject longLived = Analyze([longParticle]);
        JsonObject longCapped = Analyze([longParticle], loopLengthMaximum: 60);
        check(CandidateFrames(longLived).SequenceEqual([3721UL]) &&
            longLived["loop_length_default"]!["status"]!.GetValue<string>() == "applied" &&
            CandidateFrames(longCapped).Length == 0 && longCapped["loop_length_default"]!["status"]!.GetValue<string>() == "particle_lifetime_not_shorter_than_loop" &&
            longCapped["loop_length_default"]!["reason_zh"]!.GetValue<string>().Contains("62.016 s", StringComparison.Ordinal),
            "长寿命平稳粒子延长到寿命之后的首帧（62.016 s → 3721 帧），显式 60 秒上限仍拒绝，不放松交叉淡化前提");
        check(CandidateFrames(mixed).Length == 0 && mixed["loop_length_default"] is null,
            "未解析项里还有不满足判据的粒子（跟鼠标的 560）时不取默认长度");
        JsonObject Track(int owner, double seconds, string confidence) => new() { ["source_owner_layer_id"] = owner, ["mechanism"] = "authored_track",
            ["track_name"] = null, ["duration_seconds"] = seconds, ["playback_rate"] = 1, ["looping"] = true, ["event_driven"] = false, ["confidence"] = confidence };
        JsonObject periodic = Analyze([droplets], periods: [Track(900, 10, "high")], extraObjects: [new JsonObject { ["id"] = 900 }], extraBaked: [900]);
        JsonObject unproven = Analyze([droplets], periods: [Track(901, 10, "low")], extraObjects: [new JsonObject { ["id"] = 901 }], extraBaked: [901]);
        check(CandidateFrames(periodic)[0] == 600 && periodic["loop_length_default"] is null &&
            periodic["candidates"]!.AsArray().All(item => item!["loop_length_source"] is null) &&
            CandidateFrames(unproven).Length == 0 && unproven["loop_length_default"] is null,
            "有周期分量时长度由求解器按周期定（10 秒轨道取 600 帧），有非粒子的未解析项时不取默认长度");
    }
}
