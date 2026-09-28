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
    /// <summary>
    /// 场景相机由读输入的脚本设置（同一对象的脚本既 setCameraTransforms、又静态扫到指针/音频/时钟/媒体 API）：
    /// 画面里每一层都经这台相机看到，视角随输入变。只作注记（plan 里实时层的 input_source），不改分配；
    /// 无输入的运行时观测走不到拖动分支，依赖记录里没有这条边。
    /// </summary>
    internal bool InputDrivenCamera { get; private set; }
    /// <summary>
    /// 只因 writes_shared_script_state 实时、且按键读它的别的脚本全都带输入类原因的写者：它只服务于随输入变的层，
    /// 记 shared_state_for_input_readers。同样只作注记（plan 里的 input_source），不改分配。
    /// </summary>
    internal HashSet<int> SharedStateForInputReaders { get; } = [];
    /// <summary>
    /// 脚本按名字取图层（<c>getLayer('灯遮罩')</c>）补的写边：点击、属性变更这类回调在观测里没执行过，依赖记录里没有这条边，
    /// 被取的层（多半初始隐藏、点了才显示）会被判成不用而丢掉。getLayer 取到的层都算可能被写，写者实时则它也留实时，宁可多留。
    /// 按分配单元的闭包（<see cref="Allocation"/>）也带上这些边。
    /// </summary>
    internal JsonObject[] LookupEdges { get; private set; } = [];
    /// <summary>之前画过网格的读帧缓冲层（实时与否都在内）。没留实时的由构图阶段核对是否与之前的内容同在带场景清屏的第一组。</summary>
    internal int[] FramebufferReaders { get; private set; } = [];

    /// <summary>外部实时输入类原因（指针、音频、时钟、媒体）。结论理由也按它认"由输入驱动"（<see cref="ResidualMasking.LiveInput"/>）。</summary>
    internal static readonly string[] InputReasons = ["pointer_api", "observed_pointer", "particle_pointer_input", "active_shader_pointer_input",
        "audio_api", "observed_audio", "active_shader_audio_spectrum", "particle_audio_input", "wall_clock_api", "observed_wall_clock", "media_api"];

    private Liveness(SceneGraph graph)
    {
        objects = graph.Objects;
        Reasons = objects.Keys.ToDictionary(id => id, _ => new HashSet<string>());
    }

    /// <summary>
    /// 挂在另一份同源场景图上的副本（<see cref="AnalysisMemo"/> 在各次子分析之间共用判定结果）。集合按原插入顺序复制，
    /// reasons 的先后不变；分配阶段往副本里加原因，不动母本。
    /// </summary>
    internal Liveness CopyFor(SceneGraph graph)
    {
        var copy = new Liveness(graph) { InputDrivenCamera = InputDrivenCamera, LookupEdges = [.. LookupEdges.Select(edge => edge.DeepClone().AsObject())],
            FramebufferReaders = FramebufferReaders };
        copy.Ids.UnionWith(Ids);
        foreach (var (id, reasons) in Reasons) copy.Reasons[id] = [.. reasons];
        copy.SharedStateForInputReaders.UnionWith(SharedStateForInputReaders);
        return copy;
    }

    /// <summary>按绘制顺序列出读当前帧缓冲、且之前画过网格的层，连同之前画过网格的层。</summary>
    internal static List<(int Reader, int[] Before)> FramebufferReads(JsonArray runtimeLayers)
    {
        var reads = new List<(int Reader, int[] Before)>();
        var drawn = new List<int>();
        foreach (var layer in runtimeLayers.OfType<JsonObject>())
        {
            if (drawn.Count > 0 && layer["materials"] is JsonArray materials && materials.OfType<JsonObject>().Any(material =>
                material["textures"] is JsonArray textures && textures.Any(texture => texture?.GetValue<string>() is "_rt_default" or "_rt_FullFrameBuffer")))
                reads.Add((layer["owner"]!.GetValue<int>(), [.. drawn]));
            if (layer["has_mesh"]?.GetValue<bool>() == true) drawn.Add(layer["owner"]!.GetValue<int>());
        }
        return reads;
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
    /// <param name="retainedReaders">构图阶段查出没落在第一组的读帧缓冲层，按读帧缓冲留实时。</param>
    internal static Liveness Analyze(HybridAnalyzeRequest request, ProjectSource source, SceneGraph graph, RuntimeObservation observation,
        JsonArray sourceScriptErrors, bool parallax, int? daytimeSelector, Func<JsonObject, bool> severedRead, Func<JsonObject, bool> severedWrite,
        IReadOnlySet<int>? retainedReaders = null)
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
        // 事件触发的一次性动画轨所属层实时绘制：播放时刻不定，循环视频表达不了。加载即播的见 SingleShotAllocation.IntroSeconds。
        foreach (int owner in SingleShotAllocation.LiveOwners(observation.Trace, request.SingleShotLive)) Live(owner, SingleShotAllocation.LiveReason);
        if (request.SingleShotLive)
            foreach (int owner in SingleShotAllocation.SettledSourceOwners(graph, source, request.Assets, observation.Trace,
                request.FpsNumerator, request.FpsDenominator)) Live(owner, SingleShotAllocation.LiveReason);
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
        // runtime_layers 按场景树深度优先（即绘制顺序）列出：在它之前没有任何网格的读取层只读到场景清屏色，输入是常量，不因此实时。
        // 之前画过的内容全是静态的也不因此实时：视频组采集只渲染本组的层、第一组带场景清屏，读取层与它之前的内容同在第一组时读到的就是原作画面
        // （官方文档：合成层像相机一样记录它下面的图层）。"同在第一组"由构图阶段核对（Composer.UnsettledFramebufferReaders），
        // 不满足的经 retainedReaders 重判为实时。之前有实时层、相机随输入动、或它和之前的层被脚本经 shared / 按名字取图层牵连时仍留实时，
        // 这些要等依赖闭包之后才知道，所以在下面闭包之后判。
        // _rt_MipMappedFrameBuffer（genericimage REFLECTION）不在此列：官方 WPE 里它是上一帧的整幅合成（清屏色换成品红，最底层反光不变），
        // 属帧反馈，由预热光栅收敛（#142），不因此实时。
        var framebufferReaders = FramebufferReads(observation.RuntimeLayers);
        liveness.FramebufferReaders = [.. framebufferReaders.Select(reader => reader.Reader).Distinct()];
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
        // 对象 → 用到 / 写入的 shared 键；认不出键（混淆、下标是变量、写入的字符串下标）记 "*"，与任何键都算同一个。
        var sharedReads = new Dictionary<int, HashSet<string>>();
        var sharedWrites = new Dictionary<int, HashSet<string>>();
        HashSet<string> Keys(Dictionary<int, HashSet<string>> map, int id) => map.TryGetValue(id, out var keys) ? keys : map[id] = [];
        var cameraScripts = new HashSet<int>();
        foreach (var (id, obj) in objects)
        {
            if (obj.ContainsKey("sound")) Live(id, "soundtrack");
            if (obj.ContainsKey("camera")) Live(id, "scene_camera");
            foreach (var binding in SceneAnalyzer.Walk(obj).OfType<JsonObject>())
            {
                if (binding["script"] is not JsonValue value || !value.TryGetValue<string>(out string? text)) continue;
                string code = CapabilityScanText(text);
                foreach (Match use in Regex.Matches(code, @"\bshared\b(?:\s*\.\s*(\w+)|\s*\[\s*(['""])(\w*)\2\s*\])?"))
                    Keys(sharedReads, id).Add(use.Groups[1].Success ? use.Groups[1].Value : use.Groups[3].Success ? use.Groups[3].Value : "*");
                // 去掉字符串字面量再认写入：混淆脚本的键是 shared[_0x..('0x5',')#$]')] 这种，引号里可能有方括号。
                foreach (Match write in Regex.Matches(Regex.Replace(code, "\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'", "''"),
                    @"\bshared\s*(\.\s*\w+|\[[^\]]*\])\s*(=(?!=)|[-+*/%&|^]=|\+\+|--)"))
                    Keys(sharedWrites, id).Add(write.Groups[1].Value.StartsWith('.') ? write.Groups[1].Value[1..].Trim() : "*");
                if (Regex.IsMatch(code, @"\bnew\s+Date\b|\bDate\s*\.\s*now\b|\btimeOfDay\b") && id != daytimeSelector) Live(id, "wall_clock_api");
                if (Regex.IsMatch(code, @"\bregisterAudioBuffers\s*\(")) Live(id, "audio_api");
                if (Regex.IsMatch(code, @"\binput\s*[.\[]|\bfunction\s+cursor\w*\s*\(")) Live(id, "pointer_api");
                if (Regex.IsMatch(code, @"\bfunction\s+media\w*\s*\(")) Live(id, "media_api");
                if (Regex.IsMatch(code, @"\bsetCameraTransforms\s*\(")) cameraScripts.Add(id);
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
        liveness.LookupEdges = ScriptLookupEdges(objects, out var lookupOwners);
        // 经 shared 全局对象给别的脚本传值的写者：这种读写不进依赖记录，层烘成视频后脚本就不再执行，
        // 读它的实时脚本在成品里拿不到值（例如按 shared 值自检、不对就 destroyLayer 的防篡改脚本会把整个场景删空）。
        // 官方 WPE 里所有脚本都在跑，所以只要别的对象的脚本也用 shared，写者就留实时。
        liveness.InputDrivenCamera = cameraScripts.Any(id => liveness.Reasons[id].Overlaps(["pointer_api", "audio_api", "wall_clock_api", "media_api"]));
        foreach (int id in sharedWrites.Keys.Where(id => sharedReads.Keys.Any(user => user != id))) Live(id, "writes_shared_script_state");
        // 反过来，写者因 shared 以外的原因（输入、时钟、读实时对象……）实时时，写进去的值随运行时变，读同一个键的层烘成视频
        // 就冻在烘焙时的值上。这类读同样不进依赖记录，补成读依赖交给下面同一个闭包（沿依赖继续传）；
        // 写者只因 writes_shared_script_state 实时时写的是确定值，这条依赖摘掉，读者照常烘。
        var sharedEdges = (from writer in sharedWrites from reader in sharedReads
            where reader.Key != writer.Key && reader.Value.Any(key => key == "*" || writer.Value.Contains(key) || writer.Value.Contains("*"))
            select new JsonObject { ["owner"] = reader.Key, ["target"] = writer.Key, ["operation"] = "read" }).ToHashSet();
        // Runtime writes by a live controller make their targets live. Reads of a live mutable
        // target make the consuming animation live too. Initialization-only transforms stay snapshots.
        void CloseAll() => Close(observation.Dependencies.OfType<JsonObject>().Concat(sharedEdges).Concat(liveness.LookupEdges), liveness.Ids.Contains, liveness.Mark, ownerPerRule: true,
            dependency => sharedEdges.Contains(dependency)
                ? liveness.Reasons[dependency["target"]!.GetValue<int>()].All(reason => reason == "writes_shared_script_state")
                : severedRead(dependency), severedWrite);
        CloseAll();
        // 读帧缓冲层（见上）：已经实时的照旧记这条原因；新变实时的会让后面的读取层也变，再闭包一轮，直到不再变。
        // Keep every getLayer controller, including one whose name cannot be resolved to an edge.
        var scriptLinked = sharedReads.Keys.Concat(sharedWrites.Keys)
            .Concat(lookupOwners)
            .Concat(liveness.LookupEdges.Select(edge => edge["target"]!.GetValue<int>())).ToHashSet();
        bool promoted = true;
        while (promoted)
        {
            promoted = false;
            foreach (var (reader, before) in framebufferReaders)
                if (liveness.Ids.Contains(reader) || liveness.InputDrivenCamera || retainedReaders?.Contains(reader) == true ||
                    scriptLinked.Contains(reader) || before.Any(id => liveness.Ids.Contains(id) || scriptLinked.Contains(id)))
                    promoted |= liveness.Mark(reader, "reads_current_framebuffer");
            if (promoted) CloseAll();
        }
        foreach (int writer in sharedWrites.Keys.Where(id => liveness.Reasons[id].SetEquals(["writes_shared_script_state"])))
        {
            var readers = sharedEdges.Where(edge => edge["target"]!.GetValue<int>() == writer).Select(edge => edge["owner"]!.GetValue<int>()).ToList();
            if (readers.Count > 0 && readers.All(reader => liveness.Reasons[reader].Overlaps(InputReasons))) liveness.SharedStateForInputReaders.Add(writer);
        }
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
    /// 脚本按名字取图层的静态边（见 <see cref="LookupEdges"/>）：owner 是脚本所在对象，target 是取得到的别的层。
    /// 被取的名字：getLayer 的参数按数据流求值（字面量、脚本属性的烘焙值、局部变量、数组元素，见 ScriptTime.LayerNames）；
    /// 有一处求不出具体名字时退回：参数是字面量取这个名字，否则取脚本里引号括起的任何图层名。
    /// 没有 getLayer 的脚本不算（下拉选项的 '1'、'2' 之类会撞上同名图层）。
    /// 写边记在 visible 上：昼夜选择器按名字取受控层的那部分照常由 severedWrite 摘掉。
    /// </summary>
    internal static JsonObject[] ScriptLookupEdges(IReadOnlyDictionary<int, JsonObject> objects)
        => ScriptLookupEdges(objects, out _);

    private static JsonObject[] ScriptLookupEdges(IReadOnlyDictionary<int, JsonObject> objects, out IReadOnlySet<int> lookupOwners)
    {
        var lookupScripts = new Dictionary<int, List<(string Code, string[]? Names)>>();
        foreach (var (id, obj) in objects)
            foreach (var binding in SceneAnalyzer.Walk(obj).OfType<JsonObject>())
                if (binding["script"] is JsonValue value && value.TryGetValue<string>(out string? text) && CapabilityScanText(text) is var code &&
                    code.Contains("getLayer"))
                    (lookupScripts.TryGetValue(id, out var codes) ? codes : lookupScripts[id] = []).Add((code, ScriptTime.LayerNames(binding, obj)));
        lookupOwners = lookupScripts.Keys.ToHashSet();
        var named = objects.Where(pair => pair.Value["name"] is JsonValue name && name.TryGetValue<string>(out string? text) && text.Length > 0)
            .ToLookup(pair => pair.Value["name"]!.GetValue<string>(), pair => pair.Key);
        IEnumerable<string> LookedUp((string Code, string[]? Names) script) => script.Names ?? Quoted(script.Code);
        IEnumerable<string> Quoted(string code) => Regex.IsMatch(code, @"\bgetLayer\s*\(\s*[^'""`\s)]")
            ? named.Select(group => group.Key).Where(name => QuotesName(code, name))
            : Regex.Matches(code, @"\bgetLayer\s*\(\s*(['""`])((?:(?!\1).)*)\1").Select(match => match.Groups[2].Value);
        return (from owner in lookupScripts from name in owner.Value.SelectMany(LookedUp).Distinct() from target in named[name]
            where target != owner.Key
            select new JsonObject { ["owner"] = owner.Key, ["target"] = target, ["operation"] = "write", ["property"] = "visible" }).ToArray();
    }

    /// <summary>脚本里有没有引号括起的这个图层名（按名字 getLayer 取层的痕迹）。</summary>
    internal static bool QuotesName(string code, string name) => "'\"`".Any(q => code.Contains($"{q}{name}{q}"));

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
