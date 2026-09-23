using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Baker.Core.SceneGraph;

namespace Baker.Core;

/// <summary>
/// 实时判定阶段：哪些对象必须留在实时绘制、各自为什么（原因按首次记录的先后保存，plan 的 reasons 按这个顺序写）。
/// 依赖闭包只有 <see cref="Close"/> 一份实现，按对象判（本阶段）与按分配单元判（<see cref="Allocation"/>）都调它。
/// </summary>
internal sealed class Liveness
{
    private readonly Dictionary<int, JsonObject> objects;

    /// <summary>必须实时的对象 id。</summary>
    internal HashSet<int> Ids { get; } = [];
    /// <summary>每个对象的实时原因（非实时对象为空集）。</summary>
    internal Dictionary<int, HashSet<string>> Reasons { get; }

    private Liveness(SceneGraph graph)
    {
        objects = graph.Objects;
        Reasons = objects.Keys.ToDictionary(id => id, _ => new HashSet<string>());
    }

    /// <summary>记一条实时原因；场景外的 id 忽略。返回这个对象是否新变成实时。</summary>
    internal bool Mark(int id, string reason)
    {
        if (!objects.ContainsKey(id)) return false;
        bool added = Ids.Add(id);
        Reasons[id].Add(reason);
        return added;
    }

    /// <param name="severedRead">昼夜状态下摘掉的依赖（已冻结的视频读取），整条不参与闭包。</param>
    /// <param name="severedWrite">昼夜状态下摘掉的可见性写，只不再把目标连坐成实时。</param>
    internal static Liveness Analyze(HybridAnalyzeRequest request, ProjectSource source, SceneGraph graph, RuntimeObservation observation,
        JsonArray sourceScriptErrors, bool parallax, int? daytimeSelector, Func<JsonObject, bool> severedRead, Func<JsonObject, bool> severedWrite)
    {
        var liveness = new Liveness(graph);
        var objects = graph.Objects;
        void Live(int id, string reason) => liveness.Mark(id, reason);
        foreach (var error in sourceScriptErrors.OfType<JsonObject>())
        {
            if (Int(error["owner_layer_id"]) is not int owner || !objects.ContainsKey(owner))
                throw new InvalidDataException("A source script fault lacks a known authored owner; its allocation cannot be inferred safely.");
            Live(owner, "source_script_error");
        }
        // 一次性动画轨的所属层必须实时绘制：循环视频会让只播一次的动画每个周期重播，
        // 与原作播完即定格的画面不一致。判据与 HybridLoopService 读的是同一份轨道证据。
        foreach (int owner in SingleShotAllocation.LiveOwners(observation.Trace)) Live(owner, SingleShotAllocation.LiveReason);
        foreach (var dependency in observation.Dependencies.OfType<JsonObject>())
        {
            int owner = dependency["owner"]!.GetValue<int>();
            string operation = dependency["operation"]!.GetValue<string>();
            if (operation == "input" && !(owner == daytimeSelector && dependency["property"]?.GetValue<string>() == "wall_clock"))
                Live(owner, "observed_" + dependency["property"]!.GetValue<string>());
            if (operation == "write" && dependency["initialization"]?.GetValue<bool>() != true &&
                dependency["property"]?.GetValue<string>() == "parallaxDepth" && request.ViewMode == "preserve")
                Live(dependency["target"]!.GetValue<int>(), "runtime_parallax_depth_change");
            if (operation == "time" && dependency["property"]?.GetValue<string>() == "frametime" &&
                objects.TryGetValue(owner, out var item) && item.ContainsKey("text")) Live(owner, "live_frame_status");
        }
        // These materials consume the already-composited scene. Their input must remain the
        // current video/live composition; their upstream drawing need not remain expensive.
        foreach (var layer in observation.RuntimeLayers.OfType<JsonObject>())
            if (layer["materials"] is JsonArray materials && materials.OfType<JsonObject>().Any(material =>
                material["textures"] is JsonArray textures && textures.Any(texture => texture?.GetValue<string>() is
                    "_rt_default" or "_rt_FullFrameBuffer")))
                Live(layer["owner"]!.GetValue<int>(), "reads_current_framebuffer");
        foreach (var layer in observation.RuntimeLayers.OfType<JsonObject>())
            if (layer["materials"] is JsonArray materials)
            {
                if (materials.OfType<JsonObject>().Any(m => m["uses_audio_spectrum"] is null))
                    throw new InvalidDataException("Runtime observation predates active shader input metadata; collect a fresh trace.");
                if (materials.OfType<JsonObject>().Any(m => m["uses_audio_spectrum"]?.GetValue<bool>() == true))
                    Live(layer["owner"]!.GetValue<int>(), "active_shader_audio_spectrum");
                if (materials.OfType<JsonObject>().Any(m => m["active_uniforms"] is JsonArray uniforms &&
                    uniforms.Any(u => u?.GetValue<string>() is "g_PointerPosition" or "g_PointerPositionLast")))
                    Live(layer["owner"]!.GetValue<int>(), "active_shader_pointer_input");
                if (parallax && request.ViewMode == "preserve" && materials.OfType<JsonObject>().Any(m =>
                    m["active_uniforms"] is JsonArray uniforms && uniforms.Any(u => u?.GetValue<string>() == "g_ParallaxPosition")))
                    Live(layer["owner"]!.GetValue<int>(), "active_shader_parallax_input");
            }
        foreach (var (id, obj) in objects)
        {
            if (obj.ContainsKey("sound")) Live(id, "soundtrack");
            if (obj.ContainsKey("camera")) Live(id, "scene_camera");
            foreach (var binding in SceneAnalyzer.Walk(obj).OfType<JsonObject>())
            {
                if (binding["script"] is not JsonValue value || !value.TryGetValue<string>(out string? text)) continue;
                string code = CapabilityScanText(text);
                if (Regex.IsMatch(code, @"\bnew\s+Date\b|\bDate\s*\.\s*now\b|\btimeOfDay\b") && id != daytimeSelector) Live(id, "wall_clock_api");
                if (Regex.IsMatch(code, @"\bregisterAudioBuffers\s*\(")) Live(id, "audio_api");
                if (Regex.IsMatch(code, @"\binput\s*[.\[]|\bfunction\s+cursor\w*\s*\(")) Live(id, "pointer_api");
                if (Regex.IsMatch(code, @"\bfunction\s+media\w*\s*\(")) Live(id, "media_api");
            }
            // Particle cursor linkage is a native input path rather than a SceneScript call.
            if (SceneAnalyzer.Walk(obj).OfType<JsonObject>().Any(n => n["name"]?.GetValue<string>() == "link_mouse" ||
                n["function"]?.GetValue<string>() == "link_mouse")) Live(id, "particle_pointer_input");
            if (obj["particle"] is JsonValue particle && particle.TryGetValue<string>(out string? resource))
            {
                var definition = SceneAnalyzer.ReadResourceJson(source, request.Assets, resource);
                if (definition.ToJsonString().Contains("link_mouse", StringComparison.OrdinalIgnoreCase) ||
                    definition["controlpoint"] is JsonArray points && points.OfType<JsonObject>()
                        .Any(point => (Int(point["flags"]) & 1) == 1)) Live(id, "particle_pointer_input");
                // 粒子的音频响应与着色器音频频谱同级：外部实时输入没有源可证明的周期，这样的层不进烘焙。
                if (ParticleInputAnalysis.HasAudioInput(definition, obj)) Live(id, "particle_audio_input");
            }
        }
        // Runtime writes by a live controller make their targets live. Reads of a live mutable
        // target make the consuming animation live too. Initialization-only transforms stay snapshots.
        Close(observation.Dependencies.OfType<JsonObject>(), liveness.Ids.Contains, liveness.Mark, ownerPerRule: true, severedRead, severedWrite);
        return liveness;
    }

    /// <summary>
    /// 依赖闭包（唯一实现）：live 写者使写入目标 live；非初始化读取 live 目标使读者 live；live 读者对 6 类运行时资源属性的读取使目标 live。
    /// 反复扫依赖表直到 <paramref name="mark"/> 一轮都不再返回 true。按对象判时 <paramref name="isLive"/> 查对象集合，
    /// 按分配单元判时查对象所在单元。
    /// </summary>
    /// <param name="mark">把对象标成实时（带原因）；返回判定集合是否因此变大。</param>
    /// <param name="ownerPerRule">
    /// 资源读取那条规则是否看得到同一条依赖里刚由"读 live 目标"规则提升的读者。true = 每条规则现查（按对象判的旧写法），
    /// false = 整条依赖用开头的判定（按单元判的旧写法）。两者闭包结果相同，只改变某个对象 reasons 里原因的先后；
    /// 为 plan v3 逐字节不变而保留两种写法，C3 统一后删掉这个参数（登记在 runs/C2-plan/forwarders.txt）。
    /// </param>
    internal static void Close(IEnumerable<JsonObject> dependencies, Func<int, bool> isLive, Func<int, string, bool> mark, bool ownerPerRule,
        Func<JsonObject, bool> severedRead, Func<JsonObject, bool> severedWrite)
    {
        bool changed;
        do
        {
            changed = false;
            foreach (var dependency in dependencies)
            {
                if (severedRead(dependency)) continue;
                int owner = dependency["owner"]!.GetValue<int>(), target = dependency["target"]!.GetValue<int>();
                string operation = dependency["operation"]!.GetValue<string>();
                bool initialization = dependency["initialization"]?.GetValue<bool>() == true;
                bool ownerLive = isLive(owner), targetLive = isLive(target);
                if (operation == "write" && ownerLive && !severedWrite(dependency)) changed |= mark(target, "written_by_live_controller");
                if (operation == "read" && !initialization && targetLive) changed |= mark(owner, "reads_live_object");
                // 骨骼、动画、效果、视频纹理、纹理动画、图层合成：随读者实时变化的运行时资源。
                if (operation == "read" && (ownerPerRule ? isLive(owner) : ownerLive) && dependency["property"]?.GetValue<string>() is
                    "boneTransform" or "animation" or "effect" or "videoTexture" or "textureAnimation" or "layerComposite")
                    changed |= mark(target, "live_runtime_resource_dependency");
            }
        } while (changed);
    }

    /// <summary>
    /// 能力扫描用的脚本文本：去掉注释、保留字符串与正则字面量（否则 URL 里的 // 会把同一行后面的实时/共享 API 调用藏起来）。
    /// 含模板字符串的脚本原样返回，宁可多判实时。
    /// </summary>
    internal static string CapabilityScanText(string code)
    {
        // Keep template expressions conservative. Quotes and regex literals must protect embedded
        // comment markers, otherwise a URL can hide the remaining live/shared API calls on its line.
        if (code.Contains('`')) return code;
        return Regex.Replace(code,
            "\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'|(?<comment>/\\*[\\s\\S]*?\\*/|//[^\\r\\n]*)|/(?:\\\\.|[^/\\\\\\r\\n])+/[a-z]*",
            match => match.Groups["comment"].Success ? "" : match.Value);
    }
}
