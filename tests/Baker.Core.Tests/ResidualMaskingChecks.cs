using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// "解析周期 + 残差掩盖"规则的判据检查。夹具照抄阿米娅 3486806915 的真实形态：foliagesway 的
/// strength 常量、Math.random 重启的精灵脚本、leaves5 的随机粒子定义。规则的边界必须守住：
/// 只有"已被证明非周期或随机"且幅度有界的分量可以被掩盖，"识别不了"的一律阻断。
/// </summary>
internal static class ResidualMaskingChecks
{
    internal static void Run(Action<bool, string> check)
    {
        JsonObject Layer(int id, string name, double fraction) => new()
        { ["id"] = id, ["name"] = name, ["canvas_fraction"] = fraction };

        JsonObject FoliageOwner(int id, double? strength) => new()
        {
            ["id"] = id,
            ["effects"] = new JsonArray(new JsonObject
            {
                ["file"] = "effects/foliagesway.json",
                ["passes"] = new JsonArray(strength is double value
                    ? new JsonObject { ["constantshadervalues"] = new JsonObject { ["strength"] = value } }
                    : new JsonObject())
            })
        };

        JsonObject Unresolved(string kind, int owner, string detail, string? resource = null, int effect = 0, int pass = 0,
            bool bounded = false, string? mechanism = null)
        {
            var node = new JsonObject { ["kind"] = kind, ["owner_layer_id"] = owner, ["detail"] = detail };
            if (resource is not null)
            {
                node["resource"] = resource;
                node["effect_index"] = effect;
                node["pass_index"] = pass;
            }
            // ShaderPeriodAnalysis 写下的机制字段：幅度有没有解析上界由它说了算，不看资源名。
            node["bounded_displacement"] = bounded;
            if (mechanism is not null) node["mechanism"] = mechanism;
            return node;
        }
        // HybridLoopService 对随机重启的精灵写下的结构化字段；残差掩盖只认这些字段，不认文案。
        JsonObject RandomSprite(int owner, string detail) => new()
        {
            ["kind"] = "runtime_animation", ["owner_layer_id"] = owner, ["track_name"] = "鸟_00020",
            ["mechanism"] = "sprite", ["random_restart"] = true, ["detail"] = detail
        };

        JsonObject Plan(JsonArray unresolved, JsonArray layers) => new()
        {
            ["settings"] = new JsonObject { ["width"] = 3072, ["height"] = 1920 },
            ["canvas_width"] = 3840,
            ["canvas_height"] = 2160,
            ["layers"] = layers,
            ["loop"] = new JsonObject { ["candidates"] = new JsonArray(new JsonObject { ["frames"] = 6000 }), ["unresolved"] = unresolved }
        };

        const string FoliageResource = "shaders/effects/foliagesway.frag + shaders/effects/foliagesway.vert";
        const string FoliageDetail = "Foliage sway adds eight sines at speeduv * (1, -0.16161616, ...) rad/s; no common return exists.";
        const string RandomSpriteDetail = "Script playback restarts this sprite animation after a Math.random() delay, so it has no period at any capture length.";
        const string ParticleDetail = "A particle sprite texture period does not establish the particle system's effective period.";

        // leaves5.json 原文的骨架：发射器与初始化器全部是随机项，maxcount 与 sizerandom 给出面积上界。
        var leaves = new JsonObject
        {
            ["animationmode"] = "randomframe",
            ["emitter"] = new JsonArray(new JsonObject { ["name"] = "sphererandom", ["rate"] = 20 }),
            ["initializer"] = new JsonArray(
                new JsonObject { ["name"] = "lifetimerandom", ["min"] = 8, ["max"] = 10 },
                new JsonObject { ["name"] = "sizerandom", ["min"] = 20, ["max"] = 50 },
                new JsonObject { ["name"] = "velocityrandom" }),
            ["maxcount"] = 200
        };
        var deterministicParticle = new JsonObject
        {
            ["emitter"] = new JsonArray(new JsonObject { ["name"] = "boxemitter" }),
            ["initializer"] = new JsonArray(new JsonObject { ["name"] = "lifetimefixed", ["min"] = 4, ["max"] = 4 }),
            ["maxcount"] = 50
        };
        JsonObject Scene(params JsonObject[] objects) => new() { ["objects"] = new JsonArray(objects.Cast<JsonNode?>().ToArray()) };
        JsonObject Sprite(int id) => new() { ["id"] = id };
        JsonObject ParticleOwner(int id, string path) => new() { ["id"] = id, ["particle"] = path };
        Func<string, JsonObject?> LeavesReader = path => path == "particles/presets/leaves5.json" ? leaves : null;
        // 阿米娅那条 foliagesway 的标准形态：带结构化的有界位移机制字段。
        JsonObject Sway(int owner) => Unresolved("NonPeriodicOrDriftingMechanism", owner, FoliageDetail, FoliageResource,
            bounded: true, mechanism: ShaderPeriodAnalysis.FoliageSwayMechanism);

        // HybridLoopService 对粒子层写下的 particle_stationarity（C1–C9 判据结论）；残差掩盖只认这个字段，不看定义里的名字。
        JsonObject ParticleItem(int owner, bool stationary, double? warmup = 13, params string[] failed) => new()
        {
            ["kind"] = "runtime_animation", ["owner_layer_id"] = owner, ["mechanism"] = "particle_system", ["detail"] = ParticleDetail,
            ["particle_stationarity"] = new JsonObject
            {
                ["stationary"] = stationary,
                ["failed_conditions"] = new JsonArray([.. failed.Select(code => (JsonNode)new JsonObject
                    { ["condition"] = code.Split(' ')[0], ["code"] = code.Split(' ')[1], ["node"] = "fixture", ["value"] = null })]),
                ["warmup_seconds"] = warmup, ["lifetime_max_seconds"] = 10
            }
        };

        // 1. 可掩盖分类：六只 Math.random 重启的鸟 + 一个通过平稳随机判据的粒子系统。
        var maskableUnresolved = new JsonArray(
            RandomSprite(186, RandomSpriteDetail),
            ParticleItem(167, true));
        var maskableLayers = new JsonArray(Layer(186, "鸟_00020", 0), Layer(167, "樱花", 0));
        JsonObject maskable = ResidualMasking.Classify(Plan(maskableUnresolved, maskableLayers),
            Scene(Sprite(186), ParticleOwner(167, "particles/presets/leaves5.json")), LeavesReader);
        JsonObject[] residualLayers = (maskable["residual_layers"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        JsonObject? bird = residualLayers.SingleOrDefault(layer => layer["owner_layer_id"]?.GetValue<int>() == 186);
        JsonObject? petals = residualLayers.SingleOrDefault(layer => layer["owner_layer_id"]?.GetValue<int>() == 167);
        check(maskable["status"]?.GetValue<string>() == "residual_maskable" && residualLayers.Length == 2 &&
            (maskable["blocking_components"] as JsonArray)?.Count == 0,
            "an analytic period whose only unresolved components are proven random is classified as residual-maskable");
        check(bird?["classification"]?.GetValue<string>() == "random_sprite" &&
            bird["mechanism"]?.GetValue<string>() == "script_random_restart" && bird["maskable"]?.GetValue<bool>() == true,
            "a sprite restarted after a Math.random() delay counts as proven random rather than unrecognised");
        check(petals?["classification"]?.GetValue<string>() == "random_particle" &&
            petals["particle_stationarity"]?["stationary"]?.GetValue<bool>() == true && petals["random_components"] is null &&
            petals["warmup_seconds"]?.GetValue<double>() == 13 && maskable["max_warmup_seconds"]?.GetValue<double>() == 13 &&
            Math.Abs((petals["canvas_fraction"]?.GetValue<double>() ?? 0) - 50d * 50 / (3840d * 2160) * 200) < 1e-6,
            "a particle system that passes the stationary-random criteria is maskable, carries its warm-up and still records its canvas share");
        check(maskable["canvas_width"]?.GetValue<double>() == 3840 && maskable["output_width"]?.GetValue<double>() == 3072,
            "area shares use the projection canvas in scene units while pixel estimates use the output size");

        // 1b. 粒子只认 particle_stationarity：定义里带 random 名字但没有判据字段（旧版计划）、判据不通过、通过却没有预热，都不可掩盖；
        // 反过来，定义里一个 random 名字都没有、判据字段说平稳，照样可掩盖——判定不再看名字。
        JsonObject Particle(JsonObject item, string resource = "particles/presets/leaves5.json") => ResidualMasking.Classify(
            Plan(new JsonArray(item), new JsonArray(Layer(167, "樱花", 0))), Scene(ParticleOwner(167, resource)),
            path => path == "particles/presets/leaves5.json" ? leaves : path == "particles/presets/fixed.json" ? deterministicParticle : null);
        JsonObject BlockedParticle(JsonObject result) => result["blocking_components"]!.AsArray().OfType<JsonObject>().Single();
        JsonObject legacyParticle = Particle(Unresolved("runtime_animation", 167, ParticleDetail));
        check(legacyParticle["status"]?.GetValue<string>() == "rejected" &&
            BlockedParticle(legacyParticle)["classification"]?.GetValue<string>() == "random_particle",
            "a particle item without a stationarity verdict is refused even when its definition names random emitters");
        JsonObject failedParticle = Particle(ParticleItem(167, false, 13, "C4 controlpoint_follows_cursor", "C5 child_systems_not_recursed"));
        check(failedParticle["status"]?.GetValue<string>() == "rejected" &&
            BlockedParticle(failedParticle)["reason"]!.GetValue<string>().Contains("C4 controlpoint_follows_cursor, C5 child_systems_not_recursed", StringComparison.Ordinal),
            "a particle that fails the stationary-random criteria is refused and the reason lists the failed condition codes");
        check(Particle(ParticleItem(167, true, null))["status"]?.GetValue<string>() == "rejected",
            "a stationary particle verdict without a warm-up duration is refused rather than baked from frame zero");
        JsonObject fieldDecides = Particle(ParticleItem(167, true, 2), "particles/presets/fixed.json");
        check(fieldDecides["status"]?.GetValue<string>() == "residual_maskable",
            "the particle verdict reads the structured stationarity field, never whether a definition node is named random");

        // 1c. 预热帧数：所有可掩盖粒子层 warmup_seconds 的最大值乘帧率向上取整；没有粒子层为 0。
        JsonObject warmups = ResidualMasking.Classify(Plan(new JsonArray(ParticleItem(153, true, 4), ParticleItem(352, true, 0.795),
                RandomSprite(186, RandomSpriteDetail)), new JsonArray(Layer(153, "水滴", 0), Layer(352, "雨", 0), Layer(186, "鸟", 0))),
            Scene(ParticleOwner(153, "particles/presets/fixed.json"), ParticleOwner(352, "particles/presets/fixed.json"), Sprite(186)),
            path => deterministicParticle);
        JsonObject rainOnly = ResidualMasking.Classify(Plan(new JsonArray(ParticleItem(352, true, 0.795)), new JsonArray(Layer(352, "雨", 0))),
            Scene(ParticleOwner(352, "particles/presets/fixed.json")), path => deterministicParticle);
        check(warmups["max_warmup_seconds"]?.GetValue<double>() == 4 &&
            ResidualMasking.WarmupFrames(warmups, 60, 1) == 240 && ResidualMasking.WarmupFrames(warmups, 60000, 1001) == 240 &&
            ResidualMasking.WarmupFrames(rainOnly, 60, 1) == 48 && ResidualMasking.WarmupFrames(rainOnly, 30, 1) == 24 &&
            ResidualMasking.WarmupFrames(ResidualMasking.Classify(Plan(new JsonArray(RandomSprite(186, RandomSpriteDetail)),
                new JsonArray(Layer(186, "鸟", 0))), Scene(Sprite(186)), LeavesReader), 60, 1) == 0,
            "warm-up frames are the largest particle warm-up times the frame rate rounded up (4 s -> 240, 0.795 s -> 48 at 60 fps), zero without particles");

        // 1d. 位移类分量（uv_sway）一律不可掩盖：人眼定标判定淡化后位置跳变明显。峰值偏移照样记录，只作取证。
        JsonObject sway = ResidualMasking.Classify(Plan(new JsonArray(Sway(407), RandomSprite(186, RandomSpriteDetail)),
                new JsonArray(Layer(407, "左臂", 0.096937), Layer(186, "鸟_00020", 0))),
            Scene(FoliageOwner(407, 0.52999997), Sprite(186)), LeavesReader);
        JsonObject arm = sway["blocking_components"]!.AsArray().OfType<JsonObject>().Single();
        check(sway["status"]?.GetValue<string>() == "rejected" && arm["classification"]?.GetValue<string>() == "displacement" &&
            arm["maskable"]?.GetValue<bool>() == false && arm["mechanism"]?.GetValue<string>() == ShaderPeriodAnalysis.FoliageSwayMechanism &&
            Math.Abs((arm["peak_offset_pixels"]?.GetValue<double>() ?? 0) - 0.52999997 * 0.52999997 * 0.02 * 3072) < 0.01 &&
            arm["peak_offset_limit_pixels"] is null && arm["layer_name"]?.GetValue<string>() == "左臂" &&
            sway["residual_layers"]!.AsArray().Count == 1,
            "a bounded foliage sway component is never masked: its crossfade shows a visible position jump, so the candidate is refused and the offset is only recorded");
        check(sway["peak_offset_limit_pixels"] is null,
            "the two-percent short-edge displacement allowance is gone together with displacement masking");

        // 2. 不可掩盖仍然拒绝：识别不了的着色器机制、拿不出随机证明的粒子、读不出 strength 的位移层。
        JsonObject unsupported = ResidualMasking.Classify(
            Plan(new JsonArray(Unresolved("UnsupportedShaderMechanism", 17, "No rule matches this shader.", "shaders/effects/lightshafts.frag")),
                new JsonArray(Layer(17, "光束", 0.1))),
            Scene(FoliageOwner(17, 0.5)), LeavesReader);
        check(unsupported["status"]?.GetValue<string>() == "rejected",
            "an unrecognised shader mechanism still blocks the candidate and names the layer");
        JsonObject deterministic = ResidualMasking.Classify(
            Plan(new JsonArray(ParticleItem(167, false, 4, "C1 emitter_burst")), new JsonArray(Layer(167, "樱花", 0))),
            Scene(ParticleOwner(167, "particles/presets/fixed.json")),
            path => path == "particles/presets/fixed.json" ? deterministicParticle : null);
        check(deterministic["status"]?.GetValue<string>() == "rejected" &&
            (deterministic["blocking_components"] as JsonArray)?.OfType<JsonObject>().Single()["classification"]?.GetValue<string>() == "random_particle",
            "a particle system that fails the criteria is refused instead of being masked");
        JsonObject unknownStrength = ResidualMasking.Classify(
            Plan(new JsonArray(Sway(407)), new JsonArray(Layer(407, "左臂", 0.1))),
            Scene(FoliageOwner(407, null)), LeavesReader);
        JsonObject unknownStrengthVerdict = unknownStrength["blocking_components"]!.AsArray().OfType<JsonObject>().Single();
        check(unknownStrength["status"]?.GetValue<string>() == "rejected" && unknownStrengthVerdict["peak_offset_pixels"] is null &&
            unknownStrengthVerdict["classification"]?.GetValue<string>() == "displacement",
            "a foliage sway pass with no readable strength is refused as displacement without inventing an offset");
        JsonObject tooWide = ResidualMasking.Classify(
            Plan(new JsonArray(Sway(407)), new JsonArray(Layer(407, "左臂", 0.1))),
            Scene(FoliageOwner(407, 1.0)), LeavesReader);
        check(tooWide["status"]?.GetValue<string>() == "rejected" &&
            tooWide["blocking_components"]![0]!["peak_offset_pixels"]?.GetValue<double>() == 61.44,
            "a wide foliage sway is refused like any displacement and its measured offset is still recorded");
        // 精灵/粒子的画布占比只记录不裁决：合计超过 1（阿米娅六只鸟的包围盒合计 4.1 倍画布）也照样可掩盖，
        // 掩盖是否可接受由全分辨率的第一层残差判据实测决定。
        JsonObject hugeCoverage = ResidualMasking.Classify(
            Plan(new JsonArray(RandomSprite(186, RandomSpriteDetail), RandomSprite(197, RandomSpriteDetail), RandomSprite(195, RandomSpriteDetail)),
                new JsonArray(Layer(186, "鸟", 0.881667), Layer(197, "鸟", 1), Layer(195, "鸟", 1))),
            Scene(Sprite(186), Sprite(197), Sprite(195)), LeavesReader);
        check(hugeCoverage["status"]?.GetValue<string>() == "residual_maskable" &&
            (hugeCoverage["blocking_components"] as JsonArray)?.Count == 0 &&
            Math.Abs((hugeCoverage["sprite_canvas_coverage_total"]?.GetValue<double>() ?? 0) - 2.881667) < 1e-6 &&
            hugeCoverage["sprite_canvas_coverage_unknown_layers"]?.GetValue<int>() == 0 &&
            hugeCoverage["thresholds"]!["sprite_canvas_coverage_total"] is null,
            "a sprite canvas share above one is recorded, never a rejection: the first-layer seam residual check is the gate");
        // 面积算不出（plan 里 canvas_fraction 为 null）同样只记录：unknown_layers 计数，层仍可掩盖。
        JsonObject unknownArea = ResidualMasking.Classify(
            Plan(new JsonArray(RandomSprite(186, RandomSpriteDetail)),
                new JsonArray(new JsonObject { ["id"] = 186, ["name"] = "鸟", ["canvas_fraction"] = null })),
            Scene(Sprite(186)), LeavesReader);
        JsonObject unknownLayer = unknownArea["residual_layers"]!.AsArray().OfType<JsonObject>().Single();
        check(unknownArea["status"]?.GetValue<string>() == "residual_maskable" &&
            unknownLayer["canvas_fraction"] is null && unknownLayer["maskable"]?.GetValue<bool>() == true &&
            unknownArea["sprite_canvas_coverage_unknown_layers"]?.GetValue<int>() == 1 &&
            unknownArea["sprite_canvas_coverage_total"]?.GetValue<double>() == 0,
            "a random sprite whose canvas share is unknown keeps null in the record instead of counting as zero, and is not refused for it");
        // 只有文案里带 Math.random() 字样、没有结构化字段的项不再被当成随机精灵。
        JsonObject legacyText = ResidualMasking.Classify(
            Plan(new JsonArray(Unresolved("runtime_animation", 186, RandomSpriteDetail)), new JsonArray(Layer(186, "鸟", 0.1))),
            Scene(Sprite(186)), LeavesReader);
        check(legacyText["status"]?.GetValue<string>() == "rejected" &&
            legacyText["blocking_components"]!.AsArray().OfType<JsonObject>().Single()["classification"]?.GetValue<string>() == "unrecognized",
            "random-restart proof is read from the structured field, never matched from the detail text");

        // 2b. 位移幅度上界同样只认结构化字段：资源名里带 foliagesway、但没有 bounded_displacement 的项
        // 不再被当成有界位移；反过来，带字段的项即便资源名换了写法也照样按 uv_sway 公式判定。
        JsonObject resourceNameOnly = ResidualMasking.Classify(
            Plan(new JsonArray(Unresolved("NonPeriodicOrDriftingMechanism", 407, FoliageDetail, FoliageResource)),
                new JsonArray(Layer(407, "左臂", 0.1))),
            Scene(FoliageOwner(407, 0.52999997)), LeavesReader);
        check(resourceNameOnly["status"]?.GetValue<string>() == "rejected" &&
            resourceNameOnly["blocking_components"]!.AsArray().OfType<JsonObject>().Single()["classification"]?.GetValue<string>()
                == "proven_nonperiodic_unbounded",
            "an amplitude bound is read from the bounded-displacement field, never matched from the resource name");
        JsonObject renamedSway = ResidualMasking.Classify(
            Plan(new JsonArray(Unresolved("NonPeriodicOrDriftingMechanism", 407, FoliageDetail,
                    "shaders/workshop/renamed_sway.frag + shaders/workshop/renamed_sway.vert",
                    bounded: true, mechanism: ShaderPeriodAnalysis.FoliageSwayMechanism)),
                new JsonArray(Layer(407, "左臂", 0.1))),
            Scene(FoliageOwner(407, 0.52999997)), LeavesReader);
        JsonObject renamedLayer = renamedSway["blocking_components"]!.AsArray().OfType<JsonObject>().Single();
        check(renamedSway["status"]?.GetValue<string>() == "rejected" &&
            renamedLayer["classification"]?.GetValue<string>() == "displacement" &&
            Math.Abs((renamedLayer["peak_offset_pixels"]?.GetValue<double>() ?? 0) - 0.52999997 * 0.52999997 * 0.02 * 3072) < 0.01,
            "a bounded uv_sway mechanism is classified as displacement from the structured field whatever its shader file is called");
        // 声称有界却不是已知有界族的机制同样是位移类，一律阻断，不记偏移。
        JsonObject unknownBounded = ResidualMasking.Classify(
            Plan(new JsonArray(Unresolved("NonPeriodicOrDriftingMechanism", 407, FoliageDetail, FoliageResource,
                    bounded: true, mechanism: "uv_unknown_bound")),
                new JsonArray(Layer(407, "左臂", 0.1))),
            Scene(FoliageOwner(407, 0.52999997)), LeavesReader);
        JsonObject unknownBoundedVerdict = unknownBounded["blocking_components"]!.AsArray().OfType<JsonObject>().Single();
        check(unknownBounded["status"]?.GetValue<string>() == "rejected" &&
            unknownBoundedVerdict["classification"]?.GetValue<string>() == "displacement" &&
            unknownBoundedVerdict["mechanism"]?.GetValue<string>() == "uv_unknown_bound" && unknownBoundedVerdict["peak_offset_pixels"] is null,
            "a mechanism that claims a bound without a peak-offset formula is refused as displacement instead of masked");

        // 2c. 线性漂移（lightshafts 一族）仍然不可掩盖，而理由必须说"已被方程证明非周期"，
        // 不能写成"没有非周期证明"；数字由该 pass 的 rayspeed 与着色器方程算出。
        JsonObject ShaftOwner(int id, double? raySpeed) => new()
        {
            ["id"] = id,
            ["effects"] = new JsonArray(new JsonObject
            {
                ["file"] = "effects/lightshafts.json",
                ["passes"] = new JsonArray(raySpeed is double speed
                    ? new JsonObject { ["constantshadervalues"] = new JsonObject { ["rayspeed"] = speed } }
                    : new JsonObject())
            })
        };
        const string ShaftDetail = "Light-shaft noise UVs translate at rayspeed 0.38999999 * (0.003, 0.000375111, 0.0047111, 0.0007399) per second.";
        JsonObject drift = ResidualMasking.Classify(
            Plan(new JsonArray(Unresolved("NonPeriodicOrDriftingMechanism", 83, ShaftDetail,
                    "shaders/effects/lightshafts.frag", mechanism: ShaderPeriodAnalysis.LightShaftDriftMechanism)),
                new JsonArray(Layer(83, "光束 - 角", 0.05))),
            Scene(ShaftOwner(83, 0.38999999)), LeavesReader);
        JsonObject driftVerdict = drift["blocking_components"]!.AsArray().OfType<JsonObject>().Single();
        check(drift["status"]?.GetValue<string>() == "rejected" && driftVerdict["maskable"]?.GetValue<bool>() == false &&
            driftVerdict["classification"]?.GetValue<string>() == "proven_nonperiodic_unbounded" &&
            driftVerdict["mechanism"]?.GetValue<string>() == ShaderPeriodAnalysis.LightShaftDriftMechanism,
            "a proven non-periodic unbounded drift is refused as proven, never as a missing non-periodicity proof");
        check(Math.Abs((driftVerdict["fastest_axis_seconds"]?.GetValue<double>() ?? 0) -
                1 / (0.0047111 * 0.38999999)) < 0.01 &&
            Math.Abs((driftVerdict["slowest_axis_seconds"]?.GetValue<double>() ?? 0) -
                1 / (0.000375111 * 0.38999999)) < 0.01,
            "the drift guidance states the axis periods computed from the pass speed and never names a layer or wallpaper");
        // 计划记下的循环时长上限（loop.maximum_seconds = --loop-max-seconds）原样进拒绝理由与指引，不再写死 180。
        JsonObject longPlan = Plan(new JsonArray(Unresolved("NonPeriodicOrDriftingMechanism", 83, ShaftDetail,
                "shaders/effects/lightshafts.frag", mechanism: ShaderPeriodAnalysis.LightShaftDriftMechanism)),
            new JsonArray(Layer(83, "光束 - 角", 0.05)));
        longPlan["loop"]!["maximum_seconds"] = 3600;
        JsonObject longVerdict = ResidualMasking.Classify(longPlan, Scene(ShaftOwner(83, 0.38999999)), LeavesReader)
            ["blocking_components"]!.AsArray().OfType<JsonObject>().Single();
        check(longVerdict["loop_ceiling_seconds"]?.GetValue<double>() == 3600 && longVerdict["maskable"]?.GetValue<bool>() == false,
            "residual masking quotes the plan's own loop ceiling and still refuses the unbounded drift");

        // 3. 残差超阈值拒绝：整幅 MAE 与最差瓦片各自都能单独否决一个起点。
        const int width = 512, height = 512;
        byte[] flat = new byte[width * height * 3];
        Array.Fill(flat, (byte)100);
        byte[] shifted = new byte[flat.Length];
        Array.Fill(shifted, (byte)103);
        LoopWrapResidual global = LoopSeamMetrics.WrapResidual(flat, shifted, width, height, ResidualMasking.SeamTileSize);
        byte[] oneTile = flat.ToArray();
        for (int y = 0; y < ResidualMasking.SeamTileSize; ++y)
            for (int x = 0; x < ResidualMasking.SeamTileSize; ++x)
                for (int channel = 0; channel < 3; ++channel)
                    oneTile[(y * width + x) * 3 + channel] = 160;
        LoopWrapResidual tile = LoopSeamMetrics.WrapResidual(flat, oneTile, width, height, ResidualMasking.SeamTileSize);
        byte[] clean = flat.ToArray();
        clean[0] = 101;
        LoopWrapResidual within = LoopSeamMetrics.WrapResidual(flat, clean, width, height, ResidualMasking.SeamTileSize);
        check(Math.Abs(global.GlobalRgbMae - 3) < 1e-9 && global.GlobalRgbMae > ResidualMasking.MaximumSeamRgbMae255,
            "a three-level whole-frame wrap difference exceeds the two-level global seam residual limit");
        check(tile.WorstTileRgbMae > ResidualMasking.MaximumResidualTileRgbMae255 &&
            tile.GlobalRgbMae < ResidualMasking.MaximumSeamRgbMae255 && tile.WorstTileX == 0 && tile.WorstTileY == 0,
            "one 64px tile whose content is replaced exceeds the first-layer tile limit on its own");
        check(ResidualMasking.MaximumResidualTileRgbMae255 == 48 && ResidualMasking.MaximumSeamRgbMae255 == 2 &&
            ResidualMasking.CrossfadeSelfCheckRounding255 == 1 &&
            ResidualMasking.SearchWindowPeriods == 2 &&
            ResidualMasking.MaximumStartAttempts == 8,
            "the first layer keeps 48 per tile and 2 whole-frame, the self-check allows one rounding level, the search window stays two periods and at most eight starts are re-checked");

        // 第一层：淡化窗口内 max_k 最差瓦片 ≤ 48，整幅只看 Δ_0 ≤ 2。
        byte[] mildTile = flat.ToArray();
        for (int y = 0; y < ResidualMasking.SeamTileSize; ++y)
            for (int x = 0; x < ResidualMasking.SeamTileSize; ++x)
                for (int channel = 0; channel < 3; ++channel)
                    mildTile[(y * width + x) * 3 + channel] = 120;
        LoopWrapResidual mild = LoopSeamMetrics.WrapResidual(flat, mildTile, width, height, ResidualMasking.SeamTileSize);
        uint window = ResidualMasking.CrossfadeFrames(60, 1);
        LoopWrapResidual[] Window(LoopWrapResidual everywhere, int at = -1, LoopWrapResidual? there = null) =>
            [.. Enumerable.Range(0, (int)window).Select(k => k == at ? there! : everywhere)];
        ResidualFirstLayer mildLayer = ResidualMasking.FirstLayer(Window(mild));
        check(Math.Abs(mild.WorstTileRgbMae - 20) < 1e-9 && mildLayer.Passed && mildLayer.MaximumWorstTileRgbMae == mild.WorstTileRgbMae &&
            mildLayer.HardCutGlobalRgbMae == mild.GlobalRgbMae,
            "a twenty-level tile residual passes the first layer on its own numbers, whatever the scene's ordinary motion is " +
            "(min(p95 0.55, 48) used to reject it)");
        ResidualFirstLayer lateLayer = ResidualMasking.FirstLayer(Window(within, 12, tile));
        check(!lateLayer.Passed && lateLayer.MaximumWorstTileFrame == 12 && lateLayer.MaximumWorstTileRgbMae == tile.WorstTileRgbMae &&
            lateLayer.HardCutGlobalRgbMae == within.GlobalRgbMae && ResidualMasking.FirstLayer(Window(within)).Passed,
            "a residual that grows inside the crossfade window is judged at its largest k, not only at the hard cut");
        ResidualFirstLayer globalLayer = ResidualMasking.FirstLayer(Window(within, 0, global));
        ResidualFirstLayer laterGlobal = ResidualMasking.FirstLayer(Window(within, 5, global));
        check(!globalLayer.Passed && laterGlobal.Passed,
            "the whole-frame limit reads the hard-cut residual delta_0; per-tile peaks are what the later frames contribute");
        LoopWrapResidual Tile(double worst, double whole) => new(whole, worst, 0, 0, ResidualMasking.SeamTileSize, 255);
        check(ResidualMasking.FirstLayer([Tile(48, 2)]).Passed && !ResidualMasking.FirstLayer([Tile(48.0001, 1)]).Passed &&
            !ResidualMasking.FirstLayer([Tile(10, 2.0001)]).Passed,
            "the first layer is inclusive at exactly 48 per tile and 2 whole-frame and rejects anything above either");
        check(within.GlobalRgbMae < ResidualMasking.MaximumSeamRgbMae255 &&
            within.WorstTileRgbMae < ResidualMasking.MaximumResidualTileRgbMae255 && within.MaximumChannelDifference == 1,
            "a single-level one-pixel wrap difference stays inside both configured seam residual limits");

        // 起点排序键 max(Δ_0, Δ_stride)。旧键 max(Δ_0/25, Δ_stride·24/25) 会把 a（9.6）排在 b（11.52）前面。
        var a = new ResidualStartCandidate(0, 0.3, 20, 10);
        var b = new ResidualStartCandidate(16, 0.3, 5, 12);
        var bigStart = new ResidualStartCandidate(32, 0.3, 30, null);
        var bigWhole = new ResidualStartCandidate(48, 2.5, 1, 1);
        var bigStride = new ResidualStartCandidate(64, 0.2, 2, 30);
        ResidualStartCandidate[] ordered = ResidualMasking.OrderStartCandidates([a, bigStart, bigWhole, bigStride, b]);
        check(a.SortKey == 20 && b.SortKey == 12 && bigStart.SortKey == 30 && bigStride.SortKey == 30 &&
            ResidualMasking.StartCandidateAdmitted(a) && ResidualMasking.StartCandidateAdmitted(b) &&
            ResidualMasking.StartCandidateAdmitted(bigStart) && !ResidualMasking.StartCandidateAdmitted(bigWhole) &&
            ResidualMasking.StartCandidateAdmitted(bigStride) &&
            ordered.Select(x => x.Start).SequenceEqual(new ulong[] { 16, 0, 64, 32, 48 }),
            "start candidates rank by max(delta_0, delta_stride) after the whole-frame admission, so a small hard cut cannot hide a large residual one stride later");
        // 逐相位打分：Δ_stride 取下一个相位的 Δ_0，只在 stride 落在淡化窗口内、且下一相位存在时才有。
        LoopWrapResidual Wrap(double global, double tile) => new(global, tile, 0, 0, 64, 0);
        LoopWrapResidual[] wraps = [Wrap(0.5, 7), Wrap(1.5, 9), Wrap(0.2, 3)];
        ResidualStartCandidate[] inWindow = SeamMath.ScoreStarts(wraps, 16, 24), outside = SeamMath.ScoreStarts(wraps, 24, 24);
        check(inWindow.Select(x => x.Start).SequenceEqual(new ulong[] { 0, 16, 32 }) && inWindow[0] == new ResidualStartCandidate(0, 0.5, 7, 9) &&
            inWindow[1].StrideWorstTile == 3 && inWindow[2].StrideWorstTile is null && outside.All(x => x.StrideWorstTile is null),
            "phase scoring takes delta_stride from the next phase only when the stride lies inside the crossfade window and a next phase exists");
        // 阿米娅形态：样本排序键 53（远超旧准入 24）、整幅在限内的候选照样准入并参与排序；只有整幅超限的排到最后。
        var sampledSpike = new ResidualStartCandidate(2688, 1.3, 53, 21);
        check(ResidualMasking.StartCandidateAdmitted(sampledSpike) &&
            ResidualMasking.OrderStartCandidates([bigWhole, sampledSpike]).Select(x => x.Start).SequenceEqual(new ulong[] { 2688, 48 }),
            "a sampled sort key above the old 24 admission is still admitted when the whole frame is within 2");

        StartFallbackChecks(check, flat, width, height);
        PackedChecks(check);

        // 4. plan 字段齐全：阈值、策略名、淡化窗口与每层的证明摘要都必须能被核对。
        JsonObject thresholds = maskable["thresholds"]!.AsObject();
        check(maskable["policy"]?.GetValue<string>() == "analytic_period_with_residual_masking" &&
            thresholds["peak_offset_short_edge_fraction"] is null &&
            thresholds["seam_rgb_mae_255"]?.GetValue<double>() == 2 &&
            thresholds["residual_worst_tile_rgb_mae_255"]?.GetValue<double>() == 48 &&
            thresholds["first_layer"] is not null &&
            thresholds["crossfade_self_check"] is not null &&
            thresholds["crossfade_self_check_rounding_255"]?.GetValue<double>() == 1 &&
            thresholds["ordinary_step_percentile"] is null && thresholds["ordinary_step_sample_frames"] is null &&
            thresholds["second_layer"] is null && thresholds["hard_cut_worst_tile_rgb_mae_255_cap"] is null &&
            thresholds["seam_tile_size"]?.GetValue<int>() == 64 &&
            thresholds["crossfade_seconds"]?.GetValue<double>() == 0.4 &&
            maskable["sprite_canvas_coverage_total"] is not null && maskable["sprite_canvas_coverage_basis"] is not null &&
            residualLayers.All(layer => layer["owner_layer_id"] is not null && layer["mechanism"] is not null &&
                layer["proof"] is not null && layer["amplitude_basis"] is not null &&
                layer["canvas_fraction"] is not null && layer["source_detail"] is not null) &&
            residualLayers.Where(layer => layer["classification"]?.GetValue<string>() != "displacement")
                .All(layer => layer["canvas_fraction_basis"] is not null),
            "the residual masking record carries the policy name, every threshold and a proof summary per layer");
        check(ResidualMasking.CrossfadeFrames(60, 1) == 24 && ResidualMasking.CrossfadeFrames(30, 1) == 12 &&
            ResidualMasking.CrossfadeFrames(24000, 1001) == 10,
            "the fixed 0.4 second crossfade window resolves to whole frames at each supported rate");
        JsonObject none = ResidualMasking.Classify(Plan([], []), Scene(), LeavesReader);
        check(none["status"]?.GetValue<string>() == "no_residual" &&
            (none["residual_layers"] as JsonArray)?.Count == 0,
            "a plan with no unresolved component reports no residual instead of inventing one");
    }

    /// <summary>
    /// 透明组 packed 帧（左半预乘色 C、右半覆盖度 α）。① 预乘淡化不变量：在 packed 域逐通道线性混合后合成到任意背景，
    /// 等于两段各自合成后再线性混合（design-particle-crossfade §4）。② 残差两半分别算：覆盖度半单通道；RGB 半只统计掩码内像素
    /// （任一帧覆盖度 &gt; 0 或 RGB 非零——加性粒子只写 RGB），掩码外两帧都是透明黑的像素不进掩码；第一层取两半更大的读数。
    /// </summary>
    private static void PackedChecks(Action<bool, string> check)
    {
        const int half = 4, height = 4, tile = 4;
        byte[] Packed(Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
        {
            var frame = new byte[half * 2 * height * 3];
            for (int y = 0; y < height; ++y)
                for (int x = 0; x < half; ++x)
                {
                    (byte r, byte g, byte b, byte a) = pixel(x, y);
                    int c = (y * half * 2 + x) * 3, s = c + half * 3;
                    frame[c] = r; frame[c + 1] = g; frame[c + 2] = b;
                    frame[s] = frame[s + 1] = frame[s + 2] = a;
                }
            return frame;
        }
        // a、b 两段：左上 2×2 是普通半透明层（预乘色随覆盖度变）；右上一个像素是加性粒子（α=0、只有 RGB）；其余透明黑。
        byte[] a = Packed((x, y) => x < 2 && y < 2 ? ((byte)60, (byte)40, (byte)20, (byte)120) : x == 3 && y == 0 ? ((byte)0, (byte)0, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)0, (byte)0));
        byte[] b = Packed((x, y) => x < 2 && y < 2 ? ((byte)90, (byte)70, (byte)30, (byte)200) : x == 3 && y == 0 ? ((byte)80, (byte)80, (byte)80, (byte)0) : ((byte)0, (byte)0, (byte)0, (byte)0));

        // ① 不变量：g = (1−w)·a + w·b（逐通道），out = C + (1 − α/255)·B。
        bool linear = true;
        foreach (double w in new[] { 24.0 / 25, 12.0 / 25, 1.0 / 25 })
            foreach (double background in new[] { 0.0, 37.5, 255.0 })
                for (int p = 0; p < half * height; ++p)
                {
                    int y = p / half, x = p % half, c = (y * half * 2 + x) * 3, s = c + half * 3;
                    for (int channel = 0; channel < 3; ++channel)
                    {
                        double Out(double color, double alpha) => color + (1 - alpha / 255) * background;
                        double mixedColor = (1 - w) * a[c + channel] + w * b[c + channel], mixedAlpha = (1 - w) * a[s] + w * b[s];
                        double expected = (1 - w) * Out(a[c + channel], a[s]) + w * Out(b[c + channel], b[s]);
                        linear &= Math.Abs(Out(mixedColor, mixedAlpha) - expected) < 1e-9;
                    }
                }
        check(linear, "a packed-domain linear crossfade composites to the same pixel as crossfading the two composited images, over any background");

        // ② 两半分别算。
        PackedWrapResidual halves = LoopSeamMetrics.PackedResidual(a, b, half, height, tile);
        // RGB 半：左上 2×2 每像素 (30+30+10)/3，加性像素 80；共 16 像素一块瓦片。覆盖度半：左上 2×2 每像素 80。
        double colorTile = (4 * 70.0 / 3 + 80) / 16, coverageTile = 4 * 80.0 / 16;
        LoopWrapResidual combined = halves.Combined(half);
        check(Math.Abs(halves.Color.WorstTileRgbMae - colorTile) < 1e-9 && Math.Abs(halves.Coverage.WorstTileRgbMae - coverageTile) < 1e-9 &&
            halves.MaskedPixels == 5 && halves.HalfPixels == 16 && Math.Abs(halves.MaskFraction - 5.0 / 16) < 1e-12 &&
            halves.Color.MaximumChannelDifference == 80 && halves.Coverage.MaximumChannelDifference == 80,
            "packed residual reads the premultiplied half inside the coverage-or-emission mask (an additive pixel with zero alpha counts, empty pixels do not) and the coverage half as one channel");
        check(combined.WorstTileRgbMae == coverageTile && combined.WorstTileX == half && combined.GlobalRgbMae == Math.Max(halves.Color.GlobalRgbMae, halves.Coverage.GlobalRgbMae),
            "the first-layer reading of a packed frame is the larger half, with a coverage tile reported at its packed-image x");
    }

    /// <summary>
    /// 起点复核的记录：<see cref="LoopStartSelector"/> 的尝试顺序、每次尝试的记录、选中记录与全部被拒时的理由。
    /// 第一层读数用真实 WrapResidual 算出来的瓦片值，不手填 passed。
    /// </summary>
    private static void StartFallbackChecks(Action<bool, string> check, byte[] flat, int width, int height)
    {
        uint window = ResidualMasking.CrossfadeFrames(60, 1);
        // 64px 瓦片整块抬高 level 级，得到一个最差瓦片正好是 level 的残差。
        LoopWrapResidual Residual(int level)
        {
            byte[] raised = flat.ToArray();
            for (int y = 0; y < ResidualMasking.SeamTileSize; ++y)
                for (int x = 0; x < ResidualMasking.SeamTileSize; ++x)
                    for (int channel = 0; channel < 3; ++channel)
                        raised[(y * width + x) * 3 + channel] = checked((byte)(flat[0] + level));
            return LoopSeamMetrics.WrapResidual(flat, raised, width, height, ResidualMasking.SeamTileSize);
        }
        // k=22 单帧尖峰 54 级（阿米娅 2688 的形态）与全窗口 33 级（2848 的形态）。
        ResidualFirstLayer Spike() => ResidualMasking.FirstLayer([.. Enumerable.Range(0, (int)window).Select(k => Residual(k == 22 ? 54 : 30))]);
        ResidualFirstLayer Steady() => ResidualMasking.FirstLayer([.. Enumerable.Range(0, (int)window).Select(_ => Residual(33))]);
        JsonObject Seam(ResidualFirstLayer layer) => new()
        {
            ["status"] = layer.Passed ? "observed_within_residual_limits" : "rejected_residual_above_limits",
            ["first_layer"] = new JsonObject
            {
                ["passed"] = layer.Passed, ["maximum_worst_tile_rgb_mae_255"] = Math.Round(layer.MaximumWorstTileRgbMae, 4),
                ["maximum_worst_tile_frame"] = layer.MaximumWorstTileFrame, ["maximum_worst_tile_x"] = layer.WorstTileX,
                ["maximum_worst_tile_y"] = layer.WorstTileY, ["hard_cut_global_rgb_mae_255"] = Math.Round(layer.HardCutGlobalRgbMae, 4)
            }
        };
        JsonObject Search(params ulong[] starts) => new()
        {
            ["candidate_count"] = 375,
            ["selected"] = new JsonObject { ["start_frame"] = starts[0] },
            ["start_attempt_order"] = new JsonArray([.. starts.Select((start, index) => (JsonNode)new JsonObject
                { ["start_frame"] = start, ["sampled_sort_key"] = 20 + index, ["sampled_global_rgb_mae_255"] = 1.0 })])
        };
        // 按排序顺序逐个起点记尝试，直到第一层通过或清单用完。
        JsonArray Records(JsonObject search, Func<int, ResidualFirstLayer> measure)
        {
            JsonObject[] order = LoopStartSelector.Order(search);
            var attempts = new JsonArray();
            for (int attempt = 0; attempt < order.Length; ++attempt)
            {
                ResidualFirstLayer layer = measure(attempt);
                attempts.Add(LoopStartSelector.Attempt(attempt, order[attempt]["start_frame"]!.GetValue<ulong>(), order[attempt],
                    [("group-1", Seam(layer))]));
                if (layer.Passed) break;
            }
            return attempts;
        }

        ResidualFirstLayer spike = Spike(), steady = Steady();
        check(!spike.Passed && spike.MaximumWorstTileFrame == 22 && Math.Abs(spike.MaximumWorstTileRgbMae - 54) < 1e-9 && steady.Passed,
            "start fallback fixture: a single-frame spike at k=22 fails the first layer while a steady 33-level window passes");

        // 第一个候选全 k 超限、第二个通过 → 选第二个，两次都记下。
        JsonObject[] secondRows = Records(Search(2688, 2848, 1024), attempt => attempt == 0 ? spike : steady).OfType<JsonObject>().ToArray();
        JsonObject selected = LoopStartSelector.Selected(1, 2848);
        check(secondRows.Length == 2 &&
            secondRows[0]["start_frame"]!.GetValue<ulong>() == 2688 && secondRows[0]["status"]!.GetValue<string>() == "rejected_seam_residual" &&
            secondRows[0]["groups"]![0]!["maximum_worst_tile_frame"]!.GetValue<int>() == 22 &&
            secondRows[0]["sampled_sort_key"]!.GetValue<int>() == 20 &&
            secondRows[1]["start_frame"]!.GetValue<ulong>() == 2848 && secondRows[1]["status"]!.GetValue<string>() == "passed_first_layer" &&
            Math.Abs(secondRows[1]["groups"]![0]!["maximum_worst_tile_rgb_mae_255"]!.GetValue<double>() - 33) < 1e-9 &&
            selected["attempt_index"]!.GetValue<int>() == 1 && selected["start_frame"]!.GetValue<ulong>() == 2848,
            "a start failing the first layer on any k is recorded as rejected with its k, a passing one as passed, and the selection names its position");

        // 排序里有 10 个候选、全部超限 → 清单只取前 8 个，理由逐个列出起点与 max_k。
        ulong[] ten = [.. Enumerable.Range(0, 10).Select(index => (ulong)(index * 16))];
        JsonArray exhausted = Records(Search(ten), _ => spike);
        Message message = LoopStartSelector.RejectionReason(exhausted, 375, window);
        JsonObject localized = message.Localized();
        check(exhausted.Count == ResidualMasking.MaximumStartAttempts &&
            exhausted.OfType<JsonObject>().All(row => row["status"]!.GetValue<string>() == "rejected_seam_residual") &&
            LoopStartSelector.Order(Search(ten)).Length == 8 &&
            localized["key"]?.GetValue<string>() == "bake.residual_start_attempts_rejected",
            "the attempt order keeps at most eight starts and the rejection reason lists every recorded start with its max_k");
        // 清单缺失（旧记录）时退回选中的那一个。
        check(LoopStartSelector.Order(new JsonObject { ["selected"] = new JsonObject { ["start_frame"] = 96UL } })
                .Single()["start_frame"]!.GetValue<ulong>() == 96,
            "a record without an attempt order falls back to the selected start");
    }
}
