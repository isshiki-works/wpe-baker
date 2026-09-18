using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>SDR 辐射闭合判据：只有逐层证明输出闭于 [0,1] 才允许 hdr 场景通过 RGBA8 组捕获。</summary>
internal static class SdrRadianceClosureChecks
{
    internal static async Task RunAsync(Action<bool, string> check, string root)
    {
        // 真实的 TEX 前导字节；判据只读格式与标志位，不解码像素。
        static byte[] Tex(int format, uint flags)
        {
            var bytes = new byte[64];
            Encoding.ASCII.GetBytes("TEXV0005\0TEXI0001\0").CopyTo(bytes, 0);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), format);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(22), flags);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(26), 64);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(30), 32);
            return bytes;
        }
        static byte[] Utf8(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());
        static JsonObject Layer(int id, string name, string model) => new() {
            ["id"] = id, ["name"] = name, ["image"] = model, ["size"] = "64 32", ["origin"] = "32 16 0",
            ["scale"] = "1 1 1", ["color"] = "1 1 1", ["brightness"] = 1.0, ["alpha"] = 1.0,
            ["colorBlendMode"] = 0, ["visible"] = true };
        static JsonObject Scene(bool hdr) => new() {
            ["general"] = new JsonObject {
                ["hdr"] = hdr, ["bloom"] = true, ["clearenabled"] = true, ["clearcolor"] = "0 0 0",
                ["orthogonalprojection"] = new JsonObject { ["width"] = 64, ["height"] = 32 } },
            ["objects"] = new JsonArray(Layer(10, "Solid", "models/solid.json"), Layer(20, "Clip", "models/video.json")) };
        static JsonObject RuntimeLayer(int id, string shader, JsonArray textures) => new() {
            ["id"] = id, ["owner"] = id, ["parent"] = -1, ["visible"] = true, ["has_mesh"] = true,
            ["has_effect_layer"] = false, ["render_group"] = false,
            ["effective_parallax_depth"] = new JsonArray(0, 0),
            ["materials"] = new JsonArray(new JsonObject {
                ["shader"] = shader, ["role"] = "source", ["blend"] = 1,
                ["uses_audio_spectrum"] = false, ["uses_system_media_thumbnail"] = false,
                ["active_uniforms"] = new JsonArray(), ["textures"] = textures }) };
        static JsonObject Trace() => new() {
            ["status"] = "complete", ["runtime_dependencies"] = new JsonArray(),
            ["source_script_error_count"] = 0, ["source_script_errors"] = new JsonArray(),
            ["runtime_video_decoders"] = new JsonArray(new JsonObject {
                ["resource_key"] = "clip", ["instance_id"] = 1, ["codec"] = "h264",
                ["coded_width"] = 64, ["coded_height"] = 32, ["pixel_format"] = "yuv420p",
                ["fps_num"] = 30, ["fps_den"] = 1, ["metadata_unknown"] = false,
                ["decoder_kind"] = "sw", ["active"] = true }),
            ["runtime_video_decoder_observation"] = new JsonObject { ["status"] = "observed", ["opened_instances"] = 1 },
            ["runtime_layers"] = new JsonArray(RuntimeLayer(10, "flat", new JsonArray()),
                RuntimeLayer(20, "genericimage3", new JsonArray("clip"))) };

        async Task<JsonObject> PlanAsync(string name, bool hdr = true,
            Action<JsonObject, JsonObject, Dictionary<string, byte[]>>? mutate = null)
        {
            string directory = Path.Combine(root, "sdr-" + name);
            Directory.CreateDirectory(Path.Combine(directory, "models"));
            Directory.CreateDirectory(Path.Combine(directory, "materials"));
            JsonObject scene = Scene(hdr), trace = Trace();
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) {
                ["models/solid.json"] = Utf8(new JsonObject { ["material"] = "materials/solid.json" }),
                ["models/video.json"] = Utf8(new JsonObject { ["material"] = "materials/video.json" }),
                ["materials/solid.json"] = Utf8(new JsonObject { ["passes"] = new JsonArray(new JsonObject {
                    ["blending"] = "normal", ["shader"] = "flat", ["textures"] = new JsonArray() }) }),
                ["materials/video.json"] = Utf8(new JsonObject { ["passes"] = new JsonArray(new JsonObject {
                    ["blending"] = "translucent", ["shader"] = "genericimage3",
                    ["combos"] = new JsonObject { ["VERSION"] = 2 }, ["textures"] = new JsonArray("clip") }) }),
                ["materials/clip.tex"] = Tex(TextureContainer.FormatRgba8, 0x22) };
            mutate?.Invoke(scene, trace, files);
            foreach (var (path, bytes) in files)
                await File.WriteAllBytesAsync(Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar)), bytes);
            string scenePath = Path.Combine(directory, "scene.json");
            await File.WriteAllTextAsync(scenePath, scene.ToJsonString());
            trace["source"] = scenePath;
            string tracePath = Path.Combine(root, "sdr-" + name + "-trace.json");
            await File.WriteAllTextAsync(tracePath, trace.ToJsonString());
            return await new HybridScenePlanner(new("not-started", "not-started", "not-started", [])).AnalyzeSingleAsync(
                new(2, directory, root, Path.Combine(root, "sdr-" + name + "-plan"), 64, 32, RuntimeTraceFile: tracePath));
        }
        static JsonObject Closure(JsonObject plan) => plan["hdr_radiance_closure"]!.AsObject();
        static bool Blocked(JsonObject plan) => plan["blockers"]!.AsArray()
            .Any(item => item!.GetValue<string>().StartsWith(SdrRadianceClosure.HdrBlocker, StringComparison.Ordinal));
        static string Reasons(JsonObject plan) => string.Join(" | ", Closure(plan)["groups"]!.AsArray().OfType<JsonObject>()
            .SelectMany(group => group["reasons"]!.AsArray()).Select(reason => reason!.GetValue<string>()));
        static string Blocker(JsonObject plan) => string.Join(" | ", plan["blockers"]!.AsArray().Select(item => item!.GetValue<string>()));

        // 基线场景按 3147346398 (Minecraft Fireplace) 的 group-1 证据复刻：flat solid + genericimage3
        // 视频贴图，translucent 混合、combos 只有 VERSION、.tex 是 format=0 的视频包、解码 yuv420p。
        JsonObject closed = await PlanAsync("closed");
        var closedLayers = Closure(closed)["groups"]![0]!["per_layer"]!.AsArray().OfType<JsonObject>().ToArray();
        check(Closure(closed)["status"]!.GetValue<string>() == "closed" && !Blocked(closed) &&
            closed["video_groups"]!.AsArray().Count == 1 && closedLayers.Length == 2 &&
            closedLayers.All(layer => layer["status"]!.GetValue<string>() == "closed") &&
            closedLayers.All(layer => new[] { "R1", "R2", "R3", "R4", "R5" }.All(rule =>
                layer["checks"]!.AsArray().OfType<JsonObject>().Any(item =>
                    item["rule"]!.GetValue<string>() == rule && item["status"]!.GetValue<string>() == "pass"))) &&
            Closure(closed)["capture_point"]!.GetValue<string>() == "before_postprocessing",
            "hdr scene with built-in SDR shaders, translucent blending, RGBA8 tex and literal scalars proves closure");

        JsonObject dimmed = await PlanAsync("dimmed", mutate: (scene, _, _) => {
            scene["objects"]![1]!["alpha"] = 0.5;
            scene["objects"]![1]!["color"] = "0.5 0.25 0";
        });
        check(Closure(dimmed)["status"]!.GetValue<string>() == "closed" && !Blocked(dimmed),
            "scalars below one are still a convex combination and keep the group closed");

        JsonObject additive = await PlanAsync("additive", mutate: (_, _, files) =>
            files["materials/video.json"] = Utf8(new JsonObject { ["passes"] = new JsonArray(new JsonObject {
                ["blending"] = "additive", ["shader"] = "genericimage3",
                ["combos"] = new JsonObject { ["VERSION"] = 2 }, ["textures"] = new JsonArray("clip") }) }));
        check(Closure(additive)["status"]!.GetValue<string>() == "open" && Blocked(additive) &&
            Reasons(additive).Contains("(R2)", StringComparison.Ordinal) &&
            Reasons(additive).Contains("additive", StringComparison.Ordinal) &&
            Blocker(additive).Contains("layer 20 \"Clip\"", StringComparison.Ordinal),
            "additive blending keeps the HDR blocker and names the layer and R2");

        JsonObject bright = await PlanAsync("brightness", mutate: (scene, _, _) =>
            scene["objects"]![1]!["brightness"] = 2.0);
        check(Closure(bright)["status"]!.GetValue<string>() == "open" && Blocked(bright) &&
            Reasons(bright).Contains("(R4)", StringComparison.Ordinal) &&
            Reasons(bright).Contains("brightness", StringComparison.Ordinal),
            "brightness above one keeps the HDR blocker and names R4");

        JsonObject overColor = await PlanAsync("color", mutate: (scene, _, _) =>
            scene["objects"]![0]!["color"] = "1 1 4");
        check(Closure(overColor)["status"]!.GetValue<string>() == "open" && Blocked(overColor) &&
            Reasons(overColor).Contains("(R4)", StringComparison.Ordinal) &&
            Reasons(overColor).Contains("color component 4", StringComparison.Ordinal),
            "a color component above one keeps the HDR blocker and names R4");

        JsonObject scripted = await PlanAsync("alpha-script", mutate: (scene, _, _) =>
            scene["objects"]![1]!["alpha"] = new JsonObject { ["value"] = 1.0, ["script"] = "thisLayer.alpha = 1;" });
        check(Closure(scripted)["status"]!.GetValue<string>() == "open" && Blocked(scripted) &&
            Reasons(scripted).Contains("(R4)", StringComparison.Ordinal) &&
            Reasons(scripted).Contains("bound to a script", StringComparison.Ordinal),
            "a script-bound alpha is undecidable and keeps the HDR blocker");

        JsonObject animated = await PlanAsync("alpha-animation", mutate: (scene, _, _) =>
            scene["objects"]![1]!["alpha"] = new JsonObject { ["animation"] = new JsonObject {
                ["options"] = new JsonObject { ["fps"] = 30, ["length"] = 120, ["mode"] = "single" } } });
        check(Closure(animated)["status"]!.GetValue<string>() == "open" && Blocked(animated) &&
            Reasons(animated).Contains("(R4)", StringComparison.Ordinal) &&
            Reasons(animated).Contains("bound to an animation", StringComparison.Ordinal),
            "an animation-bound alpha is undecidable and keeps the HDR blocker");

        JsonObject custom = await PlanAsync("custom-shader", mutate: (_, trace, files) => {
            files["materials/video.json"] = Utf8(new JsonObject { ["passes"] = new JsonArray(new JsonObject {
                ["blending"] = "translucent", ["shader"] = "workshop/2084198056/effects/glow",
                ["textures"] = new JsonArray("clip") }) });
            trace["runtime_layers"]![1]!["materials"]![0]!["shader"] = "workshop/2084198056/effects/glow";
        });
        check(Closure(custom)["status"]!.GetValue<string>() == "open" && Blocked(custom) &&
            Reasons(custom).Contains("(R1)", StringComparison.Ordinal) &&
            Reasons(custom).Contains("not a built-in SDR shader", StringComparison.Ordinal),
            "a workshop shader is not a built-in SDR shader and keeps the HDR blocker");

        JsonObject combo = await PlanAsync("unknown-combo", mutate: (_, _, files) =>
            files["materials/video.json"] = Utf8(new JsonObject { ["passes"] = new JsonArray(new JsonObject {
                ["blending"] = "translucent", ["shader"] = "genericimage3",
                ["combos"] = new JsonObject { ["VERSION"] = 2, ["HDR_BOOST"] = 1 },
                ["textures"] = new JsonArray("clip") }) }));
        check(Closure(combo)["status"]!.GetValue<string>() == "open" && Blocked(combo) &&
            Reasons(combo).Contains("(R1)", StringComparison.Ordinal) &&
            Reasons(combo).Contains("HDR_BOOST", StringComparison.Ordinal),
            "an unknown material combo is undecidable and keeps the HDR blocker");

        // 渲染器把 combo 键 toupper 后才 #define（engine ShaderParser::PreShaderHeader），官方
        // materials/util/solidlayer_instance_*.json 写的就是小写 "version"：与 "VERSION" 是同一个宏，判据必须同样放行。
        JsonObject lowerVersion = await PlanAsync("lower-version", mutate: (_, _, files) =>
            files["materials/video.json"] = Utf8(new JsonObject { ["passes"] = new JsonArray(new JsonObject {
                ["blending"] = "translucent", ["shader"] = "genericimage3",
                ["combos"] = new JsonObject { ["version"] = 2 }, ["textures"] = new JsonArray("clip") }) }));
        check(Closure(lowerVersion)["status"]!.GetValue<string>() == "closed" && !Blocked(lowerVersion),
            "a lower-case version combo is the same macro as VERSION after the renderer's toupper and proves closure");

        // 反例①：大小写不敏感只对 VERSION 生效，小写的未知 combo 仍判未知。
        JsonObject lowerUnknown = await PlanAsync("lower-unknown-combo", mutate: (_, _, files) =>
            files["materials/video.json"] = Utf8(new JsonObject { ["passes"] = new JsonArray(new JsonObject {
                ["blending"] = "translucent", ["shader"] = "genericimage3",
                ["combos"] = new JsonObject { ["version"] = 2, ["hdr_boost"] = 1 }, ["textures"] = new JsonArray("clip") }) }));
        check(Closure(lowerUnknown)["status"]!.GetValue<string>() == "open" && Blocked(lowerUnknown) &&
            Reasons(lowerUnknown).Contains("(R1)", StringComparison.Ordinal) &&
            Reasons(lowerUnknown).Contains("\"hdr_boost\"", StringComparison.Ordinal),
            "a lower-case unknown combo is still undecidable and keeps the HDR blocker");

        // 反例②：同一份小写 version 材质，层亮度 2.0 仍由 R4 拒绝——combo 放行不放松标量判据。
        JsonObject lowerVersionBright = await PlanAsync("lower-version-bright", mutate: (scene, _, files) => {
            files["materials/video.json"] = Utf8(new JsonObject { ["passes"] = new JsonArray(new JsonObject {
                ["blending"] = "translucent", ["shader"] = "genericimage3",
                ["combos"] = new JsonObject { ["version"] = 2 }, ["textures"] = new JsonArray("clip") }) });
            scene["objects"]![1]!["brightness"] = 2.0;
        });
        check(Closure(lowerVersionBright)["status"]!.GetValue<string>() == "open" && Blocked(lowerVersionBright) &&
            Reasons(lowerVersionBright).Contains("(R4)", StringComparison.Ordinal) &&
            Reasons(lowerVersionBright).Contains("brightness", StringComparison.Ordinal),
            "the same lower-case version material with brightness above one is still rejected by R4");

        JsonObject effectLayer = await PlanAsync("effect-layer", mutate: (_, trace, _) =>
            trace["runtime_layers"]![1]!["has_effect_layer"] = true);
        check(Closure(effectLayer)["status"]!.GetValue<string>() == "open" && Blocked(effectLayer) &&
            Reasons(effectLayer).Contains("(R1)", StringComparison.Ordinal) &&
            Reasons(effectLayer).Contains("effect layer", StringComparison.Ordinal),
            "an observed effect layer keeps the HDR blocker and names R1");

        JsonObject wideTexture = await PlanAsync("tex-format", mutate: (_, trace, files) => {
            files["materials/clip.tex"] = Tex(10, 0x2);
            trace["runtime_video_decoders"] = new JsonArray();
            trace["runtime_video_decoder_observation"] = new JsonObject { ["status"] = "observed", ["opened_instances"] = 0 };
        });
        check(Closure(wideTexture)["status"]!.GetValue<string>() == "open" && Blocked(wideTexture) &&
            Reasons(wideTexture).Contains("(R3)", StringComparison.Ordinal) &&
            Reasons(wideTexture).Contains("TEX format 10", StringComparison.Ordinal),
            "a non 8-bit tex container keeps the HDR blocker and names R3");

        JsonObject narrowTexture = await PlanAsync("tex-format-rgba8", mutate: (_, trace, files) => {
            files["materials/clip.tex"] = Tex(TextureContainer.FormatRgba8, 0x2);
            trace["runtime_video_decoders"] = new JsonArray();
            trace["runtime_video_decoder_observation"] = new JsonObject { ["status"] = "observed", ["opened_instances"] = 0 };
        });
        check(Closure(narrowTexture)["status"]!.GetValue<string>() == "closed" && !Blocked(narrowTexture),
            "the same scene with an 8-bit tex container proves closure, so the rule reads the format, not the wallpaper");

        JsonObject tenBit = await PlanAsync("ten-bit-video", mutate: (_, trace, _) =>
            trace["runtime_video_decoders"]![0]!["pixel_format"] = "yuv420p10le");
        check(Closure(tenBit)["status"]!.GetValue<string>() == "open" && Blocked(tenBit) &&
            Reasons(tenBit).Contains("(R3)", StringComparison.Ordinal) &&
            Reasons(tenBit).Contains("yuv420p10le", StringComparison.Ordinal),
            "a 10-bit video decode keeps the HDR blocker and names R3");

        // 采样帧缓冲的层本就会被 planner 判为 live 而不进组；判据必须独立于那条路径也拒绝它。
        JsonObject feedback = await PlanAsync("feedback", mutate: (_, trace, _) =>
            trace["runtime_layers"]![1]!["materials"]![0]!["textures"] = new JsonArray("_rt_FullFrameBuffer"));
        check(feedback["layers"]!.AsArray().OfType<JsonObject>().Single(layer => layer["id"]!.GetValue<int>() == 20)
                ["allocation"]!.GetValue<string>() == "live",
            "a framebuffer-sampling layer stays live instead of entering a captured group");
        using (var feedbackSource = new ProjectSource(Path.Combine(root, "sdr-feedback")))
        {
            JsonObject scene = Scene(true), trace = Trace();
            trace["runtime_layers"]![1]!["materials"]![0]!["textures"] = new JsonArray("clip", "_rt_FullFrameBuffer");
            JsonObject verdict = SdrRadianceClosure.Evaluate(scene, new JsonObject(),
                trace["runtime_layers"]!.AsArray(), trace["runtime_video_decoders"]!.AsArray(),
                new JsonObject { ["id"] = "group-1", ["layer_ids"] = new JsonArray(10, 20), ["include_scene_clear"] = true },
                feedbackSource, root);
            string reasons = string.Join(" | ", verdict["reasons"]!.AsArray().Select(reason => reason!.GetValue<string>()));
            check(verdict["status"]!.GetValue<string>() == "open" &&
                reasons.Contains("(R5)", StringComparison.Ordinal) &&
                reasons.Contains("_rt_FullFrameBuffer", StringComparison.Ordinal),
                "a framebuffer feedback input inside a captured group fails the closure rule and names R5");
        }

        JsonObject clear = await PlanAsync("clear-color", mutate: (scene, _, _) =>
            scene["general"]!["clearcolor"] = "2 2 2");
        check(Closure(clear)["status"]!.GetValue<string>() == "open" && Blocked(clear) &&
            Reasons(clear).Contains("(R4)", StringComparison.Ordinal) &&
            Reasons(clear).Contains("scene clear color", StringComparison.Ordinal),
            "a captured scene clear color above one keeps the HDR blocker and names R4");

        JsonObject sdr = await PlanAsync("no-hdr", hdr: false);
        check(Closure(sdr)["status"]!.GetValue<string>() == "not_applicable" && !Blocked(sdr) &&
            Closure(sdr)["groups"]!.AsArray().Count == 0 && Closure(sdr)["blocker"] is null &&
            Closure(sdr)["hdr"]!.GetValue<bool>() == false,
            "a scene without hdr does not run the closure rule and gains no blocker");

        JsonObject missingRuntime = await PlanAsync("missing-runtime", mutate: (_, trace, _) =>
            trace["runtime_layers"]!.AsArray().RemoveAt(1));
        check(Closure(missingRuntime)["status"]!.GetValue<string>() == "open" && Blocked(missingRuntime) &&
            Reasons(missingRuntime).Contains("(R1)", StringComparison.Ordinal),
            "a captured layer without runtime material evidence is undecidable and keeps the HDR blocker");

        check(!TextureContainer.IsEightBitUnsignedFormat(10) && TextureContainer.IsEightBitUnsignedFormat(0) &&
            TextureContainer.TryReadHeader(Tex(0, 0x22), out var videoHeader) && videoHeader.IsVideo &&
            TextureContainer.TryReadHeader(Tex(0, 0x2), out var plainHeader) && !plainHeader.IsVideo &&
            !TextureContainer.TryReadHeader(Encoding.ASCII.GetBytes("TEXV0004\0TEXI0001\0................"), out _),
            "tex preamble parsing reads format and the video flag and rejects other container versions");
    }
}
