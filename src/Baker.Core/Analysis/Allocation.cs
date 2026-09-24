using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Baker.Core.Liveness;
using static Baker.Core.SceneGraph;

namespace Baker.Core;

/// <summary>
/// 分配阶段：把作者根拆成分配单元（静态结构节点可以把子树拆开），定视差深度、保留实时的单元与依赖闭包，
/// 再省略固定隐藏的自包含子树与用户剔除的子树。
/// </summary>
internal sealed class Allocation
{
    /// <summary>对象 → 所在分配单元（单元以其根对象 id 命名）。</summary>
    internal Dictionary<int, int> UnitOf { get; } = [];
    /// <summary>作者深度优先顺序的分配单元。</summary>
    internal int[] Order { get; private set; } = [];
    /// <summary>每个对象的脚本（已按 <see cref="CapabilityScanText"/> 去掉注释与字符串）。</summary>
    internal Dictionary<int, string[]> Scripts { get; }
    /// <summary>单元的视差深度（取单元内第一种有效深度）。</summary>
    internal Dictionary<int, (double X, double Y)> Depths { get; } = [];
    /// <summary>整单元保留实时的单元。</summary>
    internal HashSet<int> LiveUnits { get; private set; } = [];
    /// <summary>不进视频、不留实时、不进静态纹理的对象（固定隐藏子树与用户剔除）。</summary>
    internal HashSet<int> OmittedIds { get; } = [];
    /// <summary>用户剔除的根（升序去重）与它们的整棵子树。</summary>
    internal int[] ExcludedRoots { get; private set; } = [];
    internal HashSet<int> ExcludedIds { get; private set; } = [];
    /// <summary>被省略子树的可见性绑定到的壁纸属性名：导出后这些属性不再起作用。</summary>
    internal string[] FixedProperties { get; private set; } = [];
    /// <summary>最终留在实时绘制的对象。</summary>
    internal HashSet<int> LiveIds { get; private set; } = [];
    /// <summary>有脚本做动态对象查找（getLayer、thisScene、parent 等）：这时不省略隐藏子树，也不简化文字效果。</summary>
    internal bool DynamicLookup { get; private set; }

    private Allocation(SceneGraph graph) =>
        Scripts = graph.Objects.ToDictionary(pair => pair.Key, pair => SceneAnalyzer.Walk(pair.Value).OfType<JsonObject>()
            .Where(n => n["script"] is JsonValue).Select(n => CapabilityScanText(n["script"]!.GetValue<string>())).ToArray());

    /// <param name="severedRead">与 <see cref="Liveness.Analyze"/> 同一对昼夜过滤，用于按单元的依赖闭包。</param>
    internal static Allocation Plan(SceneGraph graph, RuntimeObservation observation, Liveness liveness, HybridAnalyzeRequest request,
        JsonObject properties, bool parallax, Func<JsonObject, bool> severedRead, Func<JsonObject, bool> severedWrite)
    {
        var allocation = new Allocation(graph);
        var (objects, sourceOrder, rootOf, authorRoots) = (graph.Objects, graph.SourceOrder, graph.RootOf, graph.Roots);
        var (live, reasons, scripts) = (liveness.Ids, liveness.Reasons, allocation.Scripts);
        JsonArray dependencies = observation.Dependencies, runtimeLayers = observation.RuntimeLayers;
        bool PotentialVisibility(int id) => (Resolve(objects[id]["visible"], properties)?.ToJsonString() != "false" ||
            graph.DynamicVisibility(id)) &&
            (Int(objects[id]["parent"]) is not int parent || !objects.ContainsKey(parent) || PotentialVisibility(parent));
        bool PotentialDrawing(int id) => PotentialVisibility(id) &&
            (objects[id].ContainsKey("image") || objects[id].ContainsKey("text") || objects[id].ContainsKey("particle") || objects[id].ContainsKey("model") ||
                runtimeLayers.OfType<JsonObject>().Any(layer => Int(layer["owner"]) == id && layer["has_mesh"]?.GetValue<bool>() == true));
        // Preserve existing independent overlay components that can move across later author roots.
        // Splitting their fixed text companions into a video would change that explicit foreground choice.
        var independentOverlays = authorRoots.Where(root => sourceOrder.Any(id => rootOf[id] == root && live.Contains(id)) &&
            sourceOrder.Any(id => rootOf[id] == root && PotentialDrawing(id)) &&
            authorRoots.Skip(Array.IndexOf(authorRoots, root) + 1).Any(later =>
                !sourceOrder.Any(id => rootOf[id] == later && live.Contains(id)) &&
                sourceOrder.Any(id => rootOf[id] == later && PotentialDrawing(id))) &&
            sourceOrder.Where(id => rootOf[id] == root).All(id => objects[id]["disablepropagation"]?.ToJsonString() != "true" &&
                (!PotentialDrawing(id) || objects[id].ContainsKey("text") || objects[id].ContainsKey("particle")) &&
                !reasons[id].Contains("reads_current_framebuffer")) &&
            !dependencies.OfType<JsonObject>().Any(d => d["operation"]?.GetValue<string>() is "read" or "write" &&
                ((Int(d["owner"]) is int owner && rootOf.GetValueOrDefault(owner, -1) == root) !=
                 (Int(d["target"]) is int target && rootOf.GetValueOrDefault(target, -1) == root)))).ToHashSet();
        // A structural node can share its fixed parent transform between independently allocated
        // children. Unknown state, drawing, scripts and observed writes keep its whole subtree intact.
        var structuralFields = new HashSet<string>(["id", "name", "parent", "origin", "angles", "scale", "visible",
            "alpha", "color", "solid", "disablepropagation", "parallaxDepth"], StringComparer.Ordinal);
        bool unresolvedObjectAccess = dependencies.OfType<JsonObject>().Any(d =>
            d["operation"]?.GetValue<string>() is "lookup" or "read" or "write" &&
            (Int(d["target"]) is not int target || !objects.ContainsKey(target)));
        bool StaticStructure(int id) => objects[id].All(pair => structuralFields.Contains(pair.Key)) &&
            scripts[id].Length == 0 && !live.Contains(id) && !unresolvedObjectAccess && !independentOverlays.Contains(id) &&
            HybridVideoProjection.SupportsStaticParent(objects[id], properties) &&
            (!parallax || request.ViewMode != "preserve" || objects[id]["parallaxDepth"] is null ||
                HybridVideoProjection.Vector(Resolve(objects[id]["parallaxDepth"], properties), (0, 0)) == (0d, 0d)) &&
            !SceneAnalyzer.Walk(objects[id]).OfType<JsonObject>().Any(SceneGraph.Animated) &&
            runtimeLayers.OfType<JsonObject>().Any(layer => Int(layer["owner"]) == id && layer["has_mesh"]?.GetValue<bool>() == false) &&
            !runtimeLayers.OfType<JsonObject>().Any(layer => Int(layer["owner"]) == id && layer["has_mesh"]?.GetValue<bool>() != false) &&
            !dependencies.OfType<JsonObject>().Any(d => Int(d["target"]) == id && d["operation"]?.GetValue<string>() == "write");
        var allocationOf = allocation.UnitOf;
        void Assign(int id, int unit)
        {
            allocationOf[id] = unit;
            bool split = id == unit && StaticStructure(id);
            foreach (int child in sourceOrder.Where(child => Int(objects[child]["parent"]) == id))
                Assign(child, split ? child : unit);
        }
        foreach (int root in authorRoots) Assign(root, root);
        // Depth-first source sibling order is the author's draw order, even when declarations interleave.
        var allocationOrder = new List<int>();
        void Order(int id)
        {
            if (allocationOf[id] == id) allocationOrder.Add(id);
            foreach (int child in sourceOrder.Where(child => Int(objects[child]["parent"]) == id)) Order(child);
        }
        foreach (int root in authorRoots) Order(root);
        int[] roots = allocation.Order = allocationOrder.ToArray();
        void Live(int id, string reason) => liveness.Mark(id, reason);
        foreach (int root in roots)
        {
            var draws = runtimeLayers.OfType<JsonObject>().Where(n => Int(n["owner"]) is int owner &&
                allocationOf.GetValueOrDefault(owner, -1) == root && n["has_mesh"]?.GetValue<bool>() == true && n["visible"]?.GetValue<bool>() != false).ToArray();
            var depths = draws.Where(n => n["effective_parallax_depth"] is JsonArray)
                .Select(n => HybridVideoProjection.Vector(n["effective_parallax_depth"], (0, 0))).Distinct().ToArray();
            allocation.Depths[root] = depths.FirstOrDefault();
            if (parallax && request.ViewMode == "preserve" && depths.Length > 1) Live(root, "mixed_parallax_depth_hierarchy");
            if (parallax && request.ViewMode == "preserve" && draws.Any(n => n["effective_parallax_depth"] is not JsonArray)) Live(root, "unresolved_runtime_parallax_depth");
            if (parallax && request.ViewMode == "preserve" && sourceOrder.Any(id => allocationOf[id] == root &&
                objects[id]["parallaxDepth"] is JsonObject binding && binding.ContainsKey("script"))) Live(root, "animated_parallax_depth");
        }
        foreach (int root in request.RetainLiveRootIds ?? [])
        {
            if (!rootOf.TryGetValue(root, out int actualRoot) || actualRoot != root) throw new InvalidDataException("A requested live root is not a source root.");
            foreach (int id in sourceOrder.Where(id => rootOf[id] == root)) Live(id, "retained_by_cost_trial");
        }
        // Hidden script hosts can initialize fonts or other live layers without drawing a pixel.
        // Preserve those controllers instead of turning them into empty video groups.
        foreach (int root in roots)
        {
            JsonNode? visibility = Resolve(objects[root]["visible"], properties);
            if (visibility is JsonObject binding) visibility = binding["value"];
            if (visibility?.ToJsonString() == "false" && sourceOrder.Any(id => allocationOf[id] == root && scripts[id].Length > 0))
                Live(root, "hidden_script_controller");
        }
        // 透视场景的模型经相机绘制：可能可见的相机（或脚本 setCameraTransforms 的全局相机）本身或父链因场景相机以外的原因实时，模型画面随它变。
        bool Moving(int id) => reasons[id].Any(reason => reason != "scene_camera") ||
            Int(objects[id]["parent"]) is int parent && objects.ContainsKey(parent) && Moving(parent);
        if (observation.Trace["runtime_projection"]?["active_camera_is_perspective"]?.GetValue<bool>() == true &&
            objects.Keys.Any(id => Moving(id) && (objects[id].ContainsKey("camera") && PotentialVisibility(id) ||
                scripts[id].Any(code => Regex.IsMatch(code, @"\bsetCameraTransforms\s*\(")))))
            foreach (int id in objects.Keys.Where(id => objects[id].ContainsKey("model"))) Live(id, "reads_live_object");
        var liveRoots = allocation.LiveUnits = live.Select(id => allocationOf[id]).ToHashSet();
        // A protected subtree can make additional controllers live; close their cross-unit accesses too.
        bool MarkUnit(int id, string reason)
        {
            if (!objects.ContainsKey(id)) return false;
            Live(id, reason);
            return liveRoots.Add(allocationOf[id]);
        }
        Liveness.Close(dependencies.OfType<JsonObject>(), id => allocationOf.TryGetValue(id, out int unit) && liveRoots.Contains(unit), MarkUnit,
            ownerPerRule: false, severedRead, severedWrite);
        // ponytail: leave dynamic object lookup/shared controllers alone. This small conservative
        // check only removes fixed-disabled, self-contained branches; it is not a JS optimizer.
        bool dynamicLookup = allocation.DynamicLookup = scripts.Values.SelectMany(s => s).Any(code => Regex.IsMatch(code,
            @"\b(thisScene|getLayer|getParent|setParent|globalThis|eval|Function|Reflect|Proxy|import)\b|\.\s*(parent|children)\b"));
        bool SafeHiddenSubtree(int id, HashSet<int> subtree) => !dynamicLookup &&
            !(request.RetainLiveRootIds ?? []).Contains(rootOf[id]) &&
            Resolve(objects[id]["visible"], properties)?.ToJsonString() == "false" &&
            !graph.DynamicVisibility(id) &&
            scripts[id].All(code => !Regex.IsMatch(code, @"\bthisLayer\s*\.\s*visible\b")) &&
            subtree.All(layer => objects[layer]["disablepropagation"]?.ToJsonString() != "true" &&
                !objects[layer].ContainsKey("sound") && !objects[layer].ContainsKey("camera") &&
                scripts[layer].All(code => !Regex.IsMatch(code, @"\b(shared|thisObject|this)\b|\.\s*(constructor|__proto__|prototype)\b|\bengine\b(?!\s*\.\s*(canvasSize|frametime|runtime|registerAudioBuffers|AUDIO_RESOLUTION_\d+|userProperties)\b)|\bthisLayer\b(?!\s*\.\s*(text|origin|scale|angles|alpha|color|visible)\b)"))) &&
            !dependencies.OfType<JsonObject>().Any(dependency =>
            {
                string? operation = dependency["operation"]?.GetValue<string>();
                int? owner = Int(dependency["owner"]), target = Int(dependency["target"]);
                return operation == "write" && target == id && dependency["property"]?.GetValue<string>() == "visible" ||
                    operation is "lookup" or "read" or "write" &&
                    ((owner is int ownerId && subtree.Contains(ownerId)) != (target is int targetId && subtree.Contains(targetId)));
            });
        var omittedSubtreeRoots = new List<int>();
        var omittedIds = allocation.OmittedIds;
        foreach (int id in sourceOrder)
        {
            if (omittedIds.Contains(id)) continue;
            var subtree = sourceOrder.Where(layer => graph.Within(layer, id)).ToHashSet();
            if (!SafeHiddenSubtree(id, subtree)) continue;
            omittedSubtreeRoots.Add(id);
            omittedIds.UnionWith(subtree);
        }
        // 用户显式剔除的图层及其子树，等同于在原作里把它关掉：不进视频、不留实时、不进静态纹理。
        // 复用既有的 omitted 机制，原作文件不变。
        int[] excludedRoots = allocation.ExcludedRoots = (request.ExcludedLayerIds ?? []).Distinct().OrderBy(id => id).ToArray();
        if (excludedRoots.Any(id => !objects.ContainsKey(id)))
            throw new InvalidDataException("An excluded layer id is not present in this scene.");
        var excludedIds = allocation.ExcludedIds = sourceOrder.Where(id => excludedRoots.Any(root => graph.Within(id, root))).ToHashSet();
        omittedIds.UnionWith(excludedIds);
        liveRoots.ExceptWith(omittedIds.Where(id => allocationOf[id] == id));
        allocation.FixedProperties = omittedSubtreeRoots.Select(id => (objects[id]["visible"] as JsonObject)?["user"])
            .Select(user => user is JsonValue value ? value.GetValue<string>() : user?["name"]?.GetValue<string>())
            .OfType<string>().Distinct().ToArray();
        // Each protected subtree stays intact; fixed structural ancestors are restored during export.
        var liveIds = allocation.LiveIds = sourceOrder.Where(id => liveRoots.Contains(allocationOf[id]) && !omittedIds.Contains(id)).ToHashSet();
        foreach (int id in liveIds.Where(id => !live.Contains(id))) reasons[id].Add("shares_live_hierarchy");
        return allocation;
    }
}
