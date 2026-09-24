using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Baker.Core.SceneGraph;

namespace Baker.Core;

/// <summary>
/// 构图阶段：可见性谓词、文字效果与前景置顶两项取舍、视频分组与 composition（绘制顺序）、可选实时前景根。
/// </summary>
internal sealed class Composer
{
    private readonly Dictionary<int, JsonObject> objects;
    private readonly JsonObject properties;
    private readonly HashSet<int> omittedIds, daytimeHidden, daytimeVisible;
    private readonly Dictionary<int, JsonObject> observed;
    private readonly HashSet<int> emptyText;

    /// <summary>视频组（plan.video_groups）。</summary>
    internal JsonArray Groups { get; } = new();
    /// <summary>视频组与实时根的绘制顺序（plan.composition）。</summary>
    internal JsonArray Composition { get; } = new();
    /// <summary>最终的分配单元顺序（前景置顶生效时实时覆盖层挪到最后）。</summary>
    internal int[] RootOrder { get; }
    internal JsonObject OcclusionTradeoff { get; }
    internal JsonObject TextEffectChoice { get; }
    /// <summary>可以从视频组尾部剥成实时的根（plan.optional_realtime_roots）。</summary>
    internal int[] OptionalForeground { get; }

    /// <param name="daytimeControlled">昼夜选择器控制的图层；不属于当前状态的按"保留但隐藏"处理。</param>
    /// <param name="daytimeVisible">当前状态下可见的受控图层。</param>
    internal Composer(HybridAnalyzeRequest request, ProjectSource source, JsonObject scene, JsonObject properties, SceneGraph graph,
        RuntimeObservation observation, Liveness liveness, Allocation allocation, bool parallax,
        HashSet<int> daytimeControlled, HashSet<int> daytimeVisible)
    {
        (objects, this.properties, this.daytimeVisible) = (graph.Objects, properties, daytimeVisible);
        var (sourceOrder, rootOf, reasons) = (graph.SourceOrder, graph.RootOf, liveness.Reasons);
        var (allocationOf, liveRoots, liveIds, rootDepths, scripts) =
            (allocation.UnitOf, allocation.LiveUnits, allocation.LiveIds, allocation.Depths, allocation.Scripts);
        omittedIds = allocation.OmittedIds;
        emptyText = objects.Keys.Where(id => EmptyText(objects[id], properties, scene)).ToHashSet();
        JsonArray dependencies = observation.Dependencies;
        int[] roots = allocation.Order;
        observed = observation.RuntimeLayers.OfType<JsonObject>().Where(n => Int(n["id"]) is int id && objects.ContainsKey(id))
            .GroupBy(n => n["id"]!.GetValue<int>()).ToDictionary(g => g.Key, g => g.First());
        // 状态拆分：不属于当前状态的受控层按"保留但隐藏"处理（不进视频、不实时、不删——选择器脚本在成品里仍要
        // getLayer 找到它们）；属于当前状态的受控层视为可见（探测在真实时刻跑，观测里它们多半是隐藏的）。
        daytimeHidden = daytimeControlled.Except(daytimeVisible).ToHashSet();
        bool MayBeVisible(int id) => !omittedIds.Contains(id) && !daytimeHidden.Contains(id) && (LocallyVisible(id) ||
            graph.DynamicVisibility(id) ||
            dependencies.OfType<JsonObject>().Any(d => Int(d["target"]) == id && d["operation"]?.GetValue<string>() == "write" &&
                d["property"]?.GetValue<string>() == "visible")) &&
            (Int(objects[id]["parent"]) is not int parent || !objects.ContainsKey(parent) || MayBeVisible(parent));
        // The stock fullscreen passthrough copies the framebuffer onto itself. Keep its scripts,
        // but it neither separates drawing groups nor prevents capturing the scene clear color.
        bool IdentityFramebuffer(int id) => objects[id]["image"]?.GetValue<string>() == "models/util/fullscreenlayer.json" &&
            objects[id]["effects"] is not JsonArray { Count: > 0 } &&
            !source.Contains("models/util/fullscreenlayer.json") && !source.Contains("materials/util/fullscreenlayer.json") &&
            !source.Contains("shaders/passthrough.frag") && !source.Contains("shaders/passthrough.vert");
        bool Contributes(int id) => Draws(id) && !IdentityFramebuffer(id);
        bool dynamicLookup = allocation.DynamicLookup;
        int[] textWithEffects = sourceOrder.Where(id => liveIds.Contains(id) && MayBeVisible(id) &&
            objects[id].ContainsKey("text") && objects[id]["effects"] is JsonArray { Count: > 0 }).ToArray();
        int[] simpleText = textWithEffects.Where(id => !dynamicLookup &&
            !SceneAnalyzer.Walk(objects[id]["effects"]!).OfType<JsonObject>().Any(SceneGraph.Dynamic) &&
            !reasons[id].Overlaps(["reads_current_framebuffer", "active_shader_audio_spectrum", "active_shader_pointer_input", "active_shader_parallax_input"]) &&
            !dependencies.OfType<JsonObject>().Any(d => Int(d["target"]) == id && d["property"]?.GetValue<string>() == "effect") &&
            !scripts.Where(pair => liveIds.Contains(pair.Key)).SelectMany(pair => pair.Value)
                .Any(code => Regex.IsMatch(code, @"\b(getEffect|getEffects|findEffect)\b|\.\s*effects\b"))).ToArray();
        TextEffectChoice = new JsonObject {
            ["selection"] = request.LiveTextEffects,
            ["status"] = request.LiveTextEffects == "simple" && simpleText.Length > 0 ? "applied" : simpleText.Length > 0 ? "available" : "not_needed",
            ["scope"] = "Optional appearance tradeoff: omit fixed text effects while preserving text scripts and parent transforms. Script-accessed and observed input/background-dependent effects remain intact.",
            ["simplified_layers"] = new JsonArray((request.LiveTextEffects == "simple" ? simpleText : []).Select(id => (JsonNode)new JsonObject {
                ["id"] = id, ["name"] = objects[id]["name"]?.DeepClone(), ["effect_count"] = objects[id]["effects"]!.AsArray().Count }).ToArray()),
            ["available_layer_ids"] = JsonSerializer.SerializeToNode(simpleText),
            ["protected_layer_ids"] = JsonSerializer.SerializeToNode(textWithEffects.Except(simpleText)) };
        int[] sourceRootOrder = roots;
        int lastBakedDraw = Array.FindLastIndex(roots, root => !liveRoots.Contains(root) &&
            sourceOrder.Any(id => allocationOf[id] == root && MayBeVisible(id) && Contributes(id)));
        // ponytail: explicit foreground placement only supports independent text/particle trees.
        // A nested overlay cannot cross any external drawable tree, including another live tree:
        // retaining its author parent would pull it back inside that parent's DFS position.
        // Image/model trees and framebuffer effects keep their order and can still block full-frame mode.
        int[] overlayRoots = roots.Take(Math.Max(0, lastBakedDraw)).Where(root => liveRoots.Contains(root) &&
            (rootOf[root] == root || !roots.Skip(Array.IndexOf(roots, root) + 1).Any(later =>
                rootOf[later] != rootOf[root] &&
                sourceOrder.Any(id => allocationOf[id] == later && MayBeVisible(id) && Contributes(id)))) &&
            sourceOrder.Any(id => allocationOf[id] == root && MayBeVisible(id) && Contributes(id)) &&
            !dependencies.OfType<JsonObject>().Any(d => d["operation"]?.GetValue<string>() is "read" or "write" &&
                ((Int(d["owner"]) is int owner && allocationOf.GetValueOrDefault(owner, -1) == root) !=
                 (Int(d["target"]) is int target && allocationOf.GetValueOrDefault(target, -1) == root))) &&
            sourceOrder.Where(id => allocationOf[id] == root && !omittedIds.Contains(id)).All(id =>
                objects[id]["disablepropagation"]?.ToJsonString() != "true" &&
                (!MayBeVisible(id) || !Contributes(id) || objects[id].ContainsKey("text") || objects[id].ContainsKey("particle")) &&
                !reasons[id].Contains("reads_current_framebuffer"))).ToArray();
        bool moveOverlays = request.LiveOverlayPlacement == "foreground" && overlayRoots.Length > 0;
        // 脚本按下标/顺序查公开图层表时声明顺序必须保持原作，不能整体置顶：覆盖层留在原位、不切断它所在的视频组，
        // 组视频画在组内最早成员的槽位，覆盖层接在组后（只越过同组的后续成员）。
        bool inPlace = moveOverlays && dependencies.OfType<JsonObject>().Any(d => d["operation"]?.GetValue<string>() == "query" &&
            d["property"]?.GetValue<string>() is "layer_numeric_index" or "layer_enumeration" or "layer_index" or "layer_order");
        if (moveOverlays && !inPlace) roots = roots.Except(overlayRoots).Concat(overlayRoots).ToArray();
        RootOrder = roots;
        var groups = Groups;
        var current = new List<int>();
        var rootSequence = Composition;
        var deferred = new List<(int Root, int MembersBefore)>();
        var crossed = new Dictionary<int, int[]>();
        int? ParentOf(int root) => Int(objects[root]["parent"]) is int parent && objects.ContainsKey(parent) ? parent : null;
        // 一个根是否自己可见且有可见的绘制后代。它既决定视频组能不能承担场景清屏，
        // 也是 full_frame 冲突里真正的阻挡者判据。
        bool VisibleDrawingRoot(int root) => MayBeVisible(root) &&
            sourceOrder.Any(layer => allocationOf[layer] == root && MayBeVisible(layer) && Contributes(layer));
        void Flush()
        {
            if (current.Count == 0) return;
            var ids = sourceOrder.Where(id => current.Contains(allocationOf[id]) && !omittedIds.Contains(id)).ToArray();
            string groupId = $"group-{groups.Count + 1}";
            bool includeClear = groups.Count == 0 && Resolve(scene["general"]?["clearenabled"], properties)?.ToJsonString() != "false" &&
                !(scene["general"]?["clearcolor"] is JsonObject clear && clear.ContainsKey("script")) &&
                rootSequence.OfType<JsonObject>().All(entry => entry["live_root"] is JsonValue prior &&
                    !(MayBeVisible(prior.GetValue<int>()) && sourceOrder.Any(layer =>
                        allocationOf[layer] == prior.GetValue<int>() && MayBeVisible(layer) && Contributes(layer))));
            var group = new JsonObject { ["id"] = groupId, ["root_ids"] = JsonSerializer.SerializeToNode(current),
                ["layer_ids"] = JsonSerializer.SerializeToNode(ids), ["transparent"] = !includeClear,
                ["parent_id"] = ParentOf(current[0]),
                ["parent_transform"] = ParentOf(current[0]) is int parent ? HybridVideoProjection.ParentTransform(objects, parent, properties) : null,
                ["include_scene_clear"] = includeClear,
                ["parallax_depth"] = new JsonArray(rootDepths[current[0]].X, rootDepths[current[0]].Y),
                ["capture_space"] = "scene", ["static_verified"] = false };
            // 先于这一组绘制的可见 live 根。只在存在这样的根时记录，其余场景的 plan 不受影响。
            int[] precedingVisibleLiveRoots = rootSequence.OfType<JsonObject>()
                .Select(entry => Int(entry["live_root"])).OfType<int>().Where(VisibleDrawingRoot).ToArray();
            if (precedingVisibleLiveRoots.Length > 0)
                group[SingleShotAllocation.PrecedingRootsField] = JsonSerializer.SerializeToNode(precedingVisibleLiveRoots);
            groups.Add(group);
            rootSequence.Add(new JsonObject { ["video_group"] = groupId });
            foreach (var (overlay, membersBefore) in deferred)
            {
                rootSequence.Add(new JsonObject { ["live_root"] = overlay });
                if (current.Count > membersBefore && overlayRoots.Contains(overlay)) crossed[overlay] = current.Skip(membersBefore).ToArray();
            }
            deferred.Clear();
            current.Clear();
        }
        foreach (int root in roots)
        {
            var descendants = sourceOrder.Where(id => allocationOf[id] == root).ToArray();
            bool visibleDrawing = MayBeVisible(root) && descendants.Any(id => MayBeVisible(id) && Contributes(id));
            if (!visibleDrawing)
            {
                // A hidden leaf sharing the pending video's parent cannot introduce
                // another drawable subtree. Keep its controller without splitting
                // adjacent videos; other ancestors still require a flush for DFS order.
                if (liveRoots.Contains(root))
                {
                    if (current.Count > 0 && (ParentOf(current[0]) != ParentOf(root) ||
                        sourceOrder.Any(id => Int(objects[id]["parent"]) == root))) Flush();
                    // 原位模式下成品按声明顺序排，组视频在最早成员处：夹在组内的隐藏实时根也接在组后。
                    if (inPlace && current.Count > 0) deferred.Add((root, current.Count));
                    else rootSequence.Add(new JsonObject { ["live_root"] = root });
                }
                continue;
            }
            if (liveRoots.Contains(root))
            {
                if (inPlace && current.Count > 0 && overlayRoots.Contains(root)) { deferred.Add((root, current.Count)); continue; }
                Flush(); rootSequence.Add(new JsonObject { ["live_root"] = root });
            }
            else
            {
                if (current.Count > 0 && ParentOf(current[0]) != ParentOf(root)) Flush();
                if (parallax && request.ViewMode == "preserve" && current.Count > 0 && rootDepths[current[0]] != rootDepths[root]) Flush();
                current.Add(root);
            }
        }
        Flush();
        OcclusionTradeoff = new Message("reason.foreground_occlusion").Write(new JsonObject {
            ["status"] = inPlace ? (crossed.Count > 0 ? "applied" : "not_needed") : moveOverlays ? "applied" : overlayRoots.Length > 0 ? "available" : "not_needed",
            ["selection"] = request.LiveOverlayPlacement,
            ["promoted_roots"] = new JsonArray((inPlace ? overlayRoots.Where(crossed.ContainsKey) : overlayRoots).Select(root => (JsonNode)new JsonObject {
                ["root_id"] = root, ["name"] = objects[root]["name"]?.DeepClone(),
                ["layer_names"] = JsonSerializer.SerializeToNode(sourceOrder.Where(id => allocationOf[id] == root && MayBeVisible(id) && Contributes(id))
                    .Select(id => objects[id]["name"]?.GetValue<string>() ?? id.ToString())),
                ["crossed_root_ids"] = JsonSerializer.SerializeToNode(inPlace ? crossed[root] : sourceRootOrder.Skip(Array.IndexOf(sourceRootOrder, root) + 1).Except(overlayRoots))
            }).ToArray()) }, "reason");
        if (inPlace && crossed.Count > 0) OcclusionTradeoff["in_place"] = true;
        var optionalRoots = roots.Where(root => !liveRoots.Contains(root) &&
            sourceOrder.Any(id => allocationOf[id] == root && !omittedIds.Contains(id) && (objects[id].ContainsKey("particle") ||
                SceneAnalyzer.Walk(objects[id]).OfType<JsonObject>().Any(n => n["script"] is JsonValue script &&
                    script.TryGetValue<string>(out string? code) && Regex.IsMatch(code, @"\bMath\s*\.\s*random\s*\("))))).ToHashSet();
        // Fixed foreground text can accompany a cheap particle without introducing another video.
        // It is measured too, and is only retained when the complete suffix includes a random root.
        var optionalCompanions = roots.Where(root => !liveRoots.Contains(root) && !omittedIds.Contains(root) && objects[root].ContainsKey("text") &&
            scripts[root].Length == 0 && sourceOrder.Count(id => allocationOf[id] == root) == 1 &&
            !SceneAnalyzer.Walk(objects[root]).OfType<JsonObject>().Any(n => SceneGraph.Animated(n) || n["effects"] is JsonArray { Count: > 0 })).ToHashSet();
        var eligibleForeground = optionalRoots.Concat(optionalCompanions).ToHashSet();
        // ponytail: only peel optional foreground roots off each video group. Retaining an
        // interior root splits the video and can double decode area; a layer timing cannot justify it.
        OptionalForeground = groups.OfType<JsonObject>().SelectMany((group, index) => {
            int[] suffix = group["root_ids"]!.AsArray().Skip(index == 0 ? 1 : 0).Select(n => n!.GetValue<int>())
                .Reverse().TakeWhile(eligibleForeground.Contains).Reverse().ToArray();
            return suffix.Any(optionalRoots.Contains) ? suffix : [];
        }).Concat(request.VideoLayout == "full_frame"
            ? groups.OfType<JsonObject>().Skip(1).Select(group => group["root_ids"]![0]!.GetValue<int>())
            : []).Distinct().OrderBy(root => Array.IndexOf(roots, root)).ToArray();
    }

    private bool LocallyVisible(int id)
    {
        if (omittedIds.Contains(id) || daytimeHidden.Contains(id)) return false;
        if (daytimeVisible.Contains(id)) return true;
        if (observed.TryGetValue(id, out var state) && state["visible"]?.GetValue<bool>() == false) return false;
        return Resolve(objects[id]["visible"], properties)?.ToJsonString() != "false";
    }

    /// <summary>对象在快照（与当前昼夜状态）里可见：自己可见且父链全可见。</summary>
    internal bool Visible(int id) => LocallyVisible(id) &&
        (Int(objects[id]["parent"]) is not int parent || !objects.ContainsKey(parent) || Visible(parent));

    /// <summary>对象会画出东西（图像、非空文字、粒子、模型或观测到网格），且没被省略。</summary>
    internal bool Draws(int id) => !omittedIds.Contains(id) && !emptyText.Contains(id) && (objects[id].ContainsKey("image") ||
        objects[id].ContainsKey("text") || objects[id].ContainsKey("particle") || objects[id].ContainsKey("model") ||
        observed.GetValueOrDefault(id)?["has_mesh"]?.GetValue<bool>() == true);

    /// <summary>
    /// 文字层的字面值是否为空串且没有脚本或动画绑定（绑了用户属性的按解析后的值判），且没有脚本能拿到它写字。
    /// 这种层画不出任何像素：不进视频组，HDR 闭合按 R0 不绘制处理。渲染器此时仍可能给它建网格（has_mesh），不作数。
    /// </summary>
    internal static bool EmptyText(JsonObject obj, JsonObject properties, JsonNode scene)
    {
        if (obj["text"] is not JsonNode raw) return false;
        if (raw is JsonObject binding && (binding.ContainsKey("script") || binding.ContainsKey("animation"))) return false;
        JsonNode? value = SceneGraph.Resolve(raw, properties);
        if (value is JsonObject resolved)
        {
            if (resolved.ContainsKey("script") || resolved.ContainsKey("animation")) return false;
            value = resolved["value"];
        }
        if (value is not JsonValue text || !text.TryGetValue<string>(out string? literal) || literal.Length != 0) return false;
        // 脚本要往这层写字得先拿到它：本层自己的脚本，按名字 getLayer，或按变量/枚举/父子关系取图层。
        string name = obj["name"]?.GetValue<string>() ?? "";
        return !SceneAnalyzer.Walk(obj).OfType<JsonObject>().Any(n => n["script"] is JsonValue) &&
            !SceneAnalyzer.Walk(scene).OfType<JsonObject>().Any(n => n["script"] is JsonValue script && script.TryGetValue<string>(out string? code) &&
                ("'\"`".Any(q => code.Contains($"{q}{name}{q}")) ||
                 Regex.IsMatch(code, @"getLayer\s*\(\s*[^'""`\s]|(enumerateLayers|getChildren|getParent|getLayerByIndex)")));
    }
}
