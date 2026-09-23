using System.Text;
using System.Text.Json.Nodes;
using Baker.Core;

internal static class SourceStaticLoopChecks
{
    internal static void Run(Action<bool, string> check, string outputRoot)
    {
        string root = Path.Combine(outputRoot, "source-static-loop");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "project.json"), "{\"type\":\"scene\",\"file\":\"scene.json\"}");
        File.WriteAllText(Path.Combine(root, "scene.json"), "{\"objects\":[{\"id\":1,\"image\":\"models/genericimage.json\"}]}");
        Directory.CreateDirectory(Path.Combine(root, "models"));
        Directory.CreateDirectory(Path.Combine(root, "shaders"));
        Directory.CreateDirectory(Path.Combine(root, "materials"));
        File.WriteAllText(Path.Combine(root, "models", "genericimage.json"), "{\"material\":\"materials/static.json\"}");
        File.WriteAllText(Path.Combine(root, "materials", "static.json"), "{\"passes\":[{\"shader\":\"genericimage\",\"textures\":[\"static\"]}]}");
        File.WriteAllText(Path.Combine(root, "shaders", "genericimage.vert"), "void main(){gl_Position=vec4(0.0);}");
        File.WriteAllText(Path.Combine(root, "shaders", "genericimage.frag"), "void main(){gl_FragColor=vec4(1.0);}");
        TextureContainer.WriteRgbaAsync(Path.Combine(root, "materials", "static.tex"), 1, 1, new byte[] { 1, 2, 3, 4 }).GetAwaiter().GetResult();
        WriteMippedTexture(Path.Combine(root, "materials", "mipped.tex"));
        byte[] mp4 = new byte[16]; mp4[4] = (byte)'f'; mp4[5] = (byte)'t'; mp4[6] = (byte)'y'; mp4[7] = (byte)'p';
        TextureContainer.WriteVideoAsync(Path.Combine(root, "materials", "video.tex"), WriteVideo(root, mp4), 1, 1).GetAwaiter().GetResult();
        using var source = new ProjectSource(root);
        JsonObject Scene(JsonObject? owner = null) => new() { ["objects"] = new JsonArray(owner ?? new JsonObject {
            ["id"] = 1, ["image"] = "models/genericimage.json" }) };
        JsonObject Runtime(string texture = "static", string uniform = "g_ModelViewProjectionMatrix") => new() {
            ["status"] = "complete", ["runtime_dependencies"] = new JsonArray(), ["runtime_animation_periods"] = new JsonArray(),
            ["runtime_layers"] = new JsonArray(new JsonObject { ["owner"] = 1, ["has_mesh"] = true,
                ["materials"] = new JsonArray(new JsonObject { ["uses_audio_spectrum"] = false, ["uses_system_media_thumbnail"] = false,
                    ["active_uniforms"] = new JsonArray(uniform, "g_ModelMatrix", "g_ViewProjectionMatrix", "g_EyePosition", "g_Color4",
                        "g_Texture0Rotation", "g_Texture0Translation", "g_Texture0Resolution", "g_LightsAmbient"),
                    ["textures"] = new JsonArray(texture, "", "", "", "", "", "", "") }) }) };
        JsonObject Analyze(JsonObject scene, JsonObject runtime) => HybridLoopService.Analyze(scene, source, null, runtime, [1], 60, 1);

        JsonObject staticReport = Analyze(Scene(), Runtime());
        check(staticReport["source_static"]!.GetValue<bool>() && staticReport["candidates"]!.AsArray().Count == 1 &&
            staticReport["candidates"]![0]!["frames"]!.GetValue<ulong>() == 1 && staticReport["unresolved"]!.AsArray().Count == 0,
            "complete source and runtime evidence produce one source-static frame");
        check(!Analyze(Scene(), Runtime(uniform: "g_CustomInput"))["source_static"]!.GetValue<bool>(),
            "unknown active uniforms do not become static by omission");
        check(!Analyze(Scene(), Runtime("video"))["source_static"]!.GetValue<bool>(),
            "embedded video texture remains non-static even without a period trace");
        JsonObject scriptedScene = Scene(new JsonObject { ["id"] = 1, ["script"] = "export function update() {}" });
        JsonObject scripted = Analyze(scriptedScene, Runtime());
        check(!scripted["source_static"]!.GetValue<bool>() &&
            scripted["unresolved"]!.AsArray().OfType<JsonObject>().Any(item =>
                item["kind"]?.GetValue<string>() == "source_static" && item["owner_layer_id"]?.GetValue<int>() == 1),
            "unproven script state stays non-static and identifies its owner for partial allocation");
        scriptedScene["objects"]!.AsArray().Add(new JsonObject { ["id"] = 2, ["image"] = "models/genericimage.json" });
        JsonObject allocation = HybridLoopAllocation.Explain(new JsonObject {
            ["settings"] = new JsonObject(), ["loop"] = scripted.DeepClone(),
            ["video_groups"] = new JsonArray(new JsonObject { ["layer_ids"] = new JsonArray(1, 2) }) }, scriptedScene);
        check(allocation["status"]?.GetValue<string>() == "proposed" &&
            allocation["retain_live_root_ids"]!.AsArray().Select(x => x!.GetValue<int>()).SequenceEqual([1]) &&
            allocation["remaining_baked_layer_ids"]!.AsArray().Select(x => x!.GetValue<int>()).SequenceEqual([2]),
            "one unproven controller no longer prevents trying an independent static layer");

        JsonObject sourceClock = Runtime();
        JsonObject sourceMaterial = sourceClock["runtime_layers"]![0]!["materials"]![0]!.AsObject();
        sourceMaterial["role"] = "source"; sourceMaterial["active_uniforms"]!.AsArray().Add("g_Time");
        check(Analyze(Scene(), sourceClock)["unresolved"]!.AsArray().OfType<JsonObject>().Any(item =>
                item["kind"]?.GetValue<string>() == "runtime_material" && item["owner_layer_id"]?.GetValue<int>() == 1),
            "a source material with an active time clock cannot bypass effect-only period analysis");

        JsonObject unknownRoleClock = Runtime();
        JsonObject unknownMaterial = unknownRoleClock["runtime_layers"]![0]!["materials"]![0]!.AsObject();
        unknownMaterial["role"] = "generated"; unknownMaterial["active_uniforms"]!.AsArray().Add("g_Runtime");
        check(Analyze(Scene(), unknownRoleClock)["unresolved"]!.AsArray().OfType<JsonObject>().Any(item =>
                item["kind"]?.GetValue<string>() == "runtime_material"),
            "a non-effect runtime material with an active runtime clock remains unresolved");

        // 以下钉住"沉默拒绝"的修复：求不出循环、又证明不了静止时，必须留下可追溯的理由。
        // feat/particle-stationarity 之后，没有精灵轨道的粒子层自己记一条 particle_system 未解析项（带平稳随机判据结论），
        // 静态证明因此不再启动；要钉住的仍是"不沉默"：理由点名这一层，并说清判据为什么不成立。
        JsonObject particles = Analyze(Scene(new JsonObject { ["id"] = 1, ["name"] = "Wind particles",
            ["particle"] = "models/particles.json" }), Runtime());
        check(!particles["source_static"]!.GetValue<bool>() && particles["candidates"]!.AsArray().Count == 0 &&
            particles["unresolved"]!.AsArray().OfType<JsonObject>().Any(item =>
                item["kind"]?.GetValue<string>() == "runtime_animation" && item["owner_layer_id"]?.GetValue<int>() == 1 &&
                item["mechanism"]?.GetValue<string>() == "particle_system" &&
                item["particle_stationarity"]?["stationary"]?.GetValue<bool>() == false &&
                item["detail"]!.GetValue<string>().Contains("particle_definition_unavailable", StringComparison.Ordinal)),
            "a particle layer names itself as the reason neither a loop nor a still image can be proven");

        JsonObject Effect(params string[] uniforms) => new() { ["role"] = "effect", ["uses_audio_spectrum"] = false,
            ["uses_system_media_thumbnail"] = false, ["active_uniforms"] = new JsonArray(uniforms.Select(name => (JsonNode)name!).ToArray()),
            ["textures"] = new JsonArray("_rt_node_1_layer_composite", "static") };
        JsonObject WithEffect(JsonObject effect)
        {
            JsonObject runtime = Runtime();
            runtime["runtime_layers"]![0]!["materials"]!.AsArray().Add(effect);
            return runtime;
        }
        check(Analyze(Scene(), WithEffect(Effect("g_ModelViewProjectionMatrix", "g_TexelSize", "u_radius", "u_strength")))["source_static"]!.GetValue<bool>(),
            "a workshop effect material with its own parameters and a composite render target stays provably static");
        // 合并 feat/shader-verdict-dedup 之后 role 不再是豁免判据：这个 fixture 的效果材质没进过方程规则链，
        // 它的时钟由运行时材质检查如实记一条（kind=runtime_material），静态证明因此根本不会启动。
        // 要钉住的仍然是"不能沉默"：必须有一条理由点到这个 uniform 的名字。
        bool NamesUniform(JsonObject report, string uniform) =>
            report["unresolved"]!.AsArray().OfType<JsonObject>().Any(item =>
                item["detail"]!.GetValue<string>().Contains(uniform, StringComparison.Ordinal));
        JsonObject effectClock = Analyze(Scene(), WithEffect(Effect("g_ModelViewProjectionMatrix", "u_strength", "g_Time")));
        check(!effectClock["source_static"]!.GetValue<bool>() && NamesUniform(effectClock, "g_Time"),
            "an effect material with a runtime clock stays non-static and names the uniform");

        check(Analyze(Scene(), Runtime("mipped"))["source_static"]!.GetValue<bool>(),
            "a mipmapped still texture is a proven still image");

        // LZ4 块：1 个 literal 0x00，再按距离 1 回拷 15 个字节，解出 16 个 0（一块 DXT 数据的形状）。
        WriteLz4Texture(Path.Combine(root, "materials", "lz4still.tex"), [0x1B, 0x00, 0x01, 0x00, 0x10, 0x00], 16);
        // LZ4 块：12 个 literal，正是 mp4 的 ftyp 盒头。
        WriteLz4Texture(Path.Combine(root, "materials", "lz4video.tex"),
            [0xC0, 0x00, 0x00, 0x00, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m'], 32);
        // 纯色大图：8 个 literal 之后一段超长回拷，匹配长度的扩展字节远长过纹理头预读。
        WriteLz4Texture(Path.Combine(root, "materials", "lz4solid.tex"),
            [0x8F, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x08, 0x00, .. Enumerable.Repeat((byte)0xFF, 400), 0x00], 1 << 20);
        check(Analyze(Scene(), Runtime("lz4solid"))["source_static"]!.GetValue<bool>(),
            "a long LZ4 match whose length bytes run past the header prefix still decodes the first twelve bytes");
        check(Analyze(Scene(), Runtime("lz4still"))["source_static"]!.GetValue<bool>() &&
            !Analyze(Scene(), Runtime("lz4video"))["source_static"]!.GetValue<bool>(),
            "an LZ4-compressed mip is judged by its decoded prefix: plain pixels are still, a compressed video container header is not");

        var rejectionMethod = typeof(Verdict).GetMethod("RequireTraceableRejection",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        void Require(JsonObject plan)
        {
            try { rejectionMethod.Invoke(null, [plan]); }
            catch (System.Reflection.TargetInvocationException error) when (error.InnerException is not null) { throw error.InnerException; }
        }
        JsonObject Plan(string route, JsonArray blockers, JsonArray unresolved, JsonArray? prefixCaches = null) => new()
        {
            ["route"] = route, ["effect_prefix_caches"] = prefixCaches ?? new JsonArray(),
            ["loop"] = new JsonObject { ["status"] = "no_analytic_candidate", ["candidates"] = new JsonArray() },
            ["video_groups"] = new JsonArray(new JsonObject { ["id"] = "group-1", ["layer_ids"] = new JsonArray(20, 21) }),
            ["whole_layer"] = new JsonObject { ["status"] = "unavailable", ["blockers"] = blockers,
                ["loop"] = new JsonObject { ["unresolved"] = unresolved } }
        };
        bool trapped = false;
        try { Require(Plan("whole_layer", [], [])); }
        catch (InvalidOperationException error) { trapped = error.Message.Contains("group-1=[20,21]", StringComparison.Ordinal); }
        check(trapped, "an unavailable whole layer with no blocker and no unresolved mechanism is an internal error naming its baked layers");
        Require(Plan("whole_layer", [], [new JsonObject { ["kind"] = "source_static", ["detail"] = "a recorded reason" }]));
        Require(Plan("whole_layer", ["a recorded blocker"], []));
        Require(Plan("effect_prefix", [], [], [new JsonObject { ["id"] = "prefix-1" }]));
        check(true, "a recorded blocker, an unresolved mechanism or an effect-prefix route is a traceable rejection");
        // 把不平稳的粒子层留实时之后，剩下的层可能只剩求解器的结构化空候选原因（如手工轨道公共周期超上限）：
        // 这是说清楚的拒绝，不是内部错误。顶层 loop 与 whole_layer.loop 两份副本任一带原因都算。
        JsonObject reasoned = Plan("whole_layer", [], []);
        reasoned["loop"]!["no_candidate_reason"] = new JsonObject { ["kind"] = "FixedPeriodExceedsCeiling", ["fixed_period_seconds"] = 47880 };
        Require(reasoned);
        JsonObject reasonedCopy = Plan("whole_layer", [], []);
        reasonedCopy["whole_layer"]!["loop"]!["no_candidate_reason"] = new JsonObject { ["kind"] = "NoFrameOnFixedStepSatisfiesComponents" };
        Require(reasonedCopy);
        check(true, "a structured no-candidate reason on either loop copy is a traceable rejection (particles kept live can leave nothing else)");

        // fix/narrative-rc10：求解器给出结构化空候选原因、却没有未解析机制（3757825891 layered 的重查、3661249043 --retain-live）
        // 不再掉进内部错误，而是写成 blocker。
        var solverMethod = typeof(Verdict).GetMethod("RecordSolverNoCandidateBlocker",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        JsonObject SolverPlan(string kind, JsonArray? unresolved = null)
        {
            JsonObject plan = Plan("whole_layer", [], unresolved ?? [new JsonObject { ["kind"] = "loop_allocation_fallback", ["detail"] = "note" }]);
            plan["blockers"] = new JsonArray();
            plan["status"] = "requires_loop_analysis";
            var wholeLoop = plan["whole_layer"]!["loop"]!.AsObject();
            wholeLoop["candidates"] = new JsonArray();
            wholeLoop["no_candidate_reason"] = new JsonObject { ["kind"] = kind, ["ceiling_seconds"] = 180, ["fixed_period_seconds"] = 0.8,
                ["shader_component_count"] = 7, ["runtime_period_count"] = 3, ["runtime_clock_uniform_count"] = 0 };
            return plan;
        }
        JsonObject noFrame = SolverPlan("NoFrameOnFixedStepSatisfiesComponents");
        solverMethod.Invoke(null, [noFrame]);
        solverMethod.Invoke(null, [noFrame]);
        JsonObject noFrameBlocker = noFrame["blockers_localized"]![0]!.AsObject();
        bool noFrameTraceable = true;
        try { Require(noFrame); } catch (InvalidOperationException) { noFrameTraceable = false; }
        check(noFrame["blockers"]!.AsArray().Count == 1 && noFrame["whole_layer"]!["blockers"]!.AsArray().Count == 1 &&
            noFrame["status"]!.GetValue<string>() == "requires_resolution" && noFrameTraceable &&
            noFrameBlocker["key"]?.GetValue<string>() == "blocker.loop_no_common_frame" &&
            noFrameBlocker["zh"]!.GetValue<string>().Contains("7 个着色器周期分量与 3 条运行时动画周期均已证明，但按 0.8 秒公共步长逐帧检查，180 秒循环上限内无同相位帧", StringComparison.Ordinal) &&
            noFrameBlocker["en"]!.GetValue<string>().Contains("no frame within the 180 s loop ceiling", StringComparison.Ordinal) &&
            PlanNarrative.Summarize(noFrame)["key"]!.GetValue<string>() == "summary.blocked",
            "a solver no-common-frame result without unresolved mechanisms becomes one bilingual blocker instead of an internal error");
        JsonObject fixedPeriod = SolverPlan("FixedPeriodExceedsCeiling");
        solverMethod.Invoke(null, [fixedPeriod]);
        JsonObject video = SolverPlan("NoExactVideoRetimeFrame");
        solverMethod.Invoke(null, [video]);
        check(PlanBlockers.Codes(fixedPeriod).Single() == BlockerCode.LoopFixedPeriodExceedsCeiling &&
            PlanBlockers.Codes(video).Single() == BlockerCode.LoopNoExactVideoRetime,
            "the fixed-period ceiling and single-video retime no-candidate reasons get their own blockers");
        JsonObject withMechanism = SolverPlan("NoFrameOnFixedStepSatisfiesComponents", [new JsonObject { ["kind"] = "source_static", ["detail"] = "reason" }]);
        JsonObject noTemporal = SolverPlan("NoTemporalMechanism");
        JsonObject withCandidate = SolverPlan("NoFrameOnFixedStepSatisfiesComponents");
        withCandidate["whole_layer"]!["loop"]!["candidates"]!.AsArray().Add(new JsonObject { ["frames"] = 60 });
        foreach (JsonObject untouched in new[] { withMechanism, noTemporal, withCandidate }) solverMethod.Invoke(null, [untouched]);
        bool noTemporalTrapped = false;
        noTemporal["whole_layer"]!["loop"]!["unresolved"] = new JsonArray();
        try { Require(noTemporal); } catch (InvalidOperationException) { noTemporalTrapped = true; }
        check(withMechanism["blockers"]!.AsArray().Count == 0 && noTemporal["blockers"]!.AsArray().Count == 0 &&
            withCandidate["blockers"]!.AsArray().Count == 0 && noTemporalTrapped,
            "no solver blocker when a mechanism already explains the result, when candidates exist, or for the no-temporal-mechanism reason, which the invariant still guards");

        var explainMethod = typeof(HybridScenePlanner).Assembly.GetType("Baker.Core.HybridLoopAllocation")!
            .GetMethod("Explain", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        JsonObject explained = (JsonObject)explainMethod.Invoke(null, [
            new JsonObject { ["video_groups"] = new JsonArray(new JsonObject { ["id"] = "group-1", ["layer_ids"] = new JsonArray(1) }) },
            Scene()])!;
        check(explained["status"]!.GetValue<string>() == "not_applicable" &&
            explained["reason"]!.GetValue<string>().Contains("particle system", StringComparison.Ordinal),
            "a smaller bake allocation that cannot help says why instead of returning nothing");
    }

    /// <summary>官方 assets 里的通用贴图（util/white）就是四级 mipmap 的普通静止图。</summary>
    private static void WriteMippedTexture(string path)
    {
        using var writer = new BinaryWriter(File.Create(path), Encoding.ASCII);
        writer.Write(Encoding.ASCII.GetBytes("TEXV0005\0TEXI0001\0"));
        writer.Write(0); writer.Write(2);
        writer.Write(2); writer.Write(2); writer.Write(2); writer.Write(2); writer.Write(0);
        writer.Write(Encoding.ASCII.GetBytes("TEXB0001\0"));
        writer.Write(1); writer.Write(4);
        writer.Write(2); writer.Write(2); writer.Write(16);
        writer.Write(new byte[16]);
    }

    /// <summary>TEXB0003：一张图、FreeImage 类型 -1、一级 mip，mip 数据以 LZ4 块存储。</summary>
    private static void WriteLz4Texture(string path, byte[] block, int decodedSize)
    {
        using var writer = new BinaryWriter(File.Create(path), Encoding.ASCII);
        writer.Write(Encoding.ASCII.GetBytes("TEXV0005\0TEXI0001\0"));
        writer.Write(7); writer.Write(2);
        writer.Write(4); writer.Write(4); writer.Write(4); writer.Write(4); writer.Write(0);
        writer.Write(Encoding.ASCII.GetBytes("TEXB0003\0"));
        writer.Write(1); writer.Write(-1);
        writer.Write(1); writer.Write(4); writer.Write(4);
        writer.Write(1); writer.Write(decodedSize); writer.Write(block.Length);
        writer.Write(block);
    }

    private static string WriteVideo(string root, byte[] bytes)
    {
        string path = Path.Combine(root, "video.mp4");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
