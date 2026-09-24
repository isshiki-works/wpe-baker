using System.Text.Json.Nodes;
using static Baker.Core.SceneGraph;

namespace Baker.Core;

/// <summary>
/// 成品场景的对象表装配：按计划的 composition 把视频组换成替换层、把实时层与它们的父级和依赖按原位置保留，
/// 再核对绘制顺序与公开图层查询。分析侧（LayoutAdmission 的层级冲突检查，只有 id/parent 的几何骨架）与
/// 烘焙侧（真实成品）共用这一份。
/// </summary>
internal static class SceneAssembler
{
    // 非实时对象保留下来时要去掉的绘制相关键。
    private static readonly string[] NonLiveDrawKeys = ["image", "model", "text", "particle", "sound", "effects", "puppet"];

    private static bool HasScriptCode(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out string? code) && !string.IsNullOrWhiteSpace(code);

    /// <summary>
    /// 去掉绘制键之后，对象自身或它的某个属性绑定上还带 script 字段。
    /// 只看对象这一层和它的直接属性，不按壁纸或脚本内容做特判。
    /// </summary>
    internal static bool CarriesRetainableScript(JsonObject obj) =>
        HasScriptCode(obj["script"]) ||
        obj.Where(property => !NonLiveDrawKeys.Contains(property.Key))
            .Any(property => property.Value is JsonObject binding && HasScriptCode(binding["script"]));

    /// <summary>
    /// 原作里"不绘制但带脚本"的根对象：计划里不是实时、不在视频组、没被省略或排除，也不绘制，
    /// 整棵子树里没有实时层、视频层或视频组的挂载父级（那些情况已经由父级保留路径按原位置输出）。
    /// 它们的脚本可能经 shared 之类的全局对象给保留的实时脚本提供函数，查找记录抓不到这种依赖，
    /// 所以按源顺序原样保留（去掉绘制键）。
    /// </summary>
    internal static int[] ScriptRootIds(IReadOnlyDictionary<int, JsonObject> originalObjects, JsonObject plan)
    {
        var layerInfo = plan["layers"]!.AsArray().OfType<JsonObject>().ToDictionary(Id);
        var liveIds = plan["live_layer_ids"]!.AsArray().Select(n => n!.GetValue<int>()).ToHashSet();
        var omitted = (plan["omitted_snapshot_layer_ids"] as JsonArray ?? []).Select(n => n!.GetValue<int>()).ToHashSet();
        var excluded = (plan["excluded_layer_ids"] as JsonArray ?? []).Select(n => Int(n)).OfType<int>().ToHashSet();
        var groups = (plan["video_groups"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var videoIds = groups.SelectMany(group => (group["layer_ids"] as JsonArray ?? []).Select(n => Int(n)).OfType<int>()).ToHashSet();
        var groupParents = groups.Select(group => Int(group["parent_id"])).OfType<int>().ToHashSet();
        bool IsRoot(JsonObject obj) => Int(obj["parent"]) is not int parent || !originalObjects.ContainsKey(parent);
        bool SubtreeRetainedElsewhere(int id) =>
            liveIds.Contains(id) || videoIds.Contains(id) || groupParents.Contains(id) ||
            originalObjects.Where(pair => Int(pair.Value["parent"]) == id).Any(pair => SubtreeRetainedElsewhere(pair.Key));
        var roots = new List<int>();
        foreach (var (id, obj) in originalObjects)
        {
            if (!IsRoot(obj) || !layerInfo.TryGetValue(id, out var layer)) continue;
            if (omitted.Contains(id) || excluded.Contains(id)) continue;
            if (layer["allocation"] is JsonValue allocation && allocation.GetValue<string>() != "inactive") continue;
            if (layer["drawable"] is not JsonValue drawable || !drawable.TryGetValue(out bool draws) || draws) continue;
            if (CarriesRetainableScript(obj) && !SubtreeRetainedElsewhere(id)) roots.Add(id);
        }
        return roots.ToArray();
    }

    /// <summary>成品对象表：昼夜动态导出走 DaytimeSplit 的装配，其余走按分配的装配；两条路都过公开图层查询检查。</summary>
    internal static JsonArray AssembleObjects(IReadOnlyDictionary<int, JsonObject> originalObjects, JsonObject plan,
        IReadOnlyDictionary<string, JsonObject> replacements, JsonArray dependencies)
    {
        if (DaytimeSplit.PrepareDynamicExport(originalObjects, plan, dependencies) is { } daytime)
        {
            JsonArray dynamicObjects = DaytimeSplit.AssembleDynamic(originalObjects, daytime, replacements);
            GuardPublicLayerQueries(originalObjects.Values, dynamicObjects.OfType<JsonObject>(), dependencies);
            return dynamicObjects;
        }
        return AssembleAllocationObjects(originalObjects, plan, replacements, dependencies);
    }

    // 纯分配/层级装配也供只有 id/parent 的几何骨架检查使用；真实成品仍从 AssembleObjects 做完整昼夜重验。
    internal static JsonArray AssembleAllocationObjects(IReadOnlyDictionary<int, JsonObject> originalObjects, JsonObject plan,
        IReadOnlyDictionary<string, JsonObject> replacements, JsonArray dependencies)
    {
        var layerInfo = plan["layers"]!.AsArray().OfType<JsonObject>().ToDictionary(Id);
        var liveIds = plan["live_layer_ids"]!.AsArray().Select(n => n!.GetValue<int>()).ToHashSet();
        var omitted = (plan["omitted_snapshot_layer_ids"] as JsonArray ?? []).Select(n => n!.GetValue<int>()).ToHashSet();
        var finalObjects = new JsonArray();
        var emitted = new HashSet<int>();
        var expected = new List<int>();
        var videos = new List<(JsonObject Video, JsonObject Group)>();
        var sourceDrawOrder = new List<int>();
        void SourceOrder(int id)
        {
            sourceDrawOrder.Add(id);
            foreach (var (child, obj) in originalObjects.Where(pair => Int(pair.Value["parent"]) == id)) SourceOrder(child);
        }
        foreach (var (id, obj) in originalObjects.Where(pair => Int(pair.Value["parent"]) is not int parent || !originalObjects.ContainsKey(parent))) SourceOrder(id);
        void Emit(int id)
        {
            if (!originalObjects.TryGetValue(id, out var original) || !emitted.Add(id)) return;
            if (omitted.Contains(id)) throw new Blocker(BlockerCode.OmittedSnapshotDependency).ToException();
            if (Int(original["parent"]) is int parent && originalObjects.ContainsKey(parent)) Emit(parent);
            var obj = original.DeepClone().AsObject();
            if (!liveIds.Contains(id))
                foreach (string key in NonLiveDrawKeys) obj.Remove(key);
            finalObjects.Add(obj);
        }
        // 不绘制但带脚本的根对象按源顺序放在最前：保证它们的 init 先于保留的实时脚本执行。
        // 它们不绘制，不进 expected，不影响下面的视频/实时绘制顺序校验。
        foreach (int id in ScriptRootIds(originalObjects, plan)) Emit(id);
        // 光源不绘制，却照亮开了 LIGHTING 的实时层；丢掉它，实时层在光源附近会变暗。原样保留，同样不进 expected。
        foreach (var (id, obj) in originalObjects)
            if (obj.ContainsKey("light") && !omitted.Contains(id)) Emit(id);
        foreach (var entry in plan["composition"]!.AsArray().OfType<JsonObject>())
        {
            if (entry["video_group"] is JsonValue groupName && replacements.TryGetValue(groupName.GetValue<string>(), out var replacement))
            {
                var group = plan["video_groups"]!.AsArray().OfType<JsonObject>().Single(g => g["id"]!.GetValue<string>() == groupName.GetValue<string>());
                if (Int(replacement["parent"]) != Int(group["parent_id"]))
                    throw new Blocker(BlockerCode.ReplacementParentMismatch).ToException();
                if (Int(replacement["parent"]) is int parent) Emit(parent);
                var video = replacement.DeepClone().AsObject();
                finalObjects.Add(video);
                videos.Add((video, group));
                expected.Add(Id(replacement));
            }
            else if (Int(entry["live_root"]) is int unit)
                foreach (int id in sourceDrawOrder)
                    if (liveIds.Contains(id) && Int(layerInfo[id]["allocation_root"] ?? layerInfo[id]["root"]) == unit)
                    {
                        Emit(id);
                        if (layerInfo[id]["drawable"]?.GetValue<bool>() == true) expected.Add(id);
                    }
        }
        foreach (var dependency in dependencies.OfType<JsonObject>())
            if (Int(dependency["owner"]) is int owner && liveIds.Contains(owner) &&
                Int(dependency["target"]) is int target && originalObjects.ContainsKey(target)) Emit(target);

        // Native FinalizeScene appends siblings in declaration order; EmitSceneNode traverses them
        // depth first. Verify parented videos occupy the planned sibling positions.
        bool KeepsDrawOrder(JsonArray objects, List<int> order)
        {
            var all = objects.OfType<JsonObject>().ToArray();
            var ids = all.Select(Id).ToHashSet();
            var orderIds = order.ToHashSet();
            var actual = new List<int>();
            void Visit(int id)
            {
                if (orderIds.Contains(id)) actual.Add(id);
                foreach (var child in all.Where(obj => Int(obj["parent"]) == id)) Visit(Id(child));
            }
            foreach (var obj in all.Where(obj => Int(obj["parent"]) is not int parent || !ids.Contains(parent)))
                Visit(Id(obj));
            return actual.SequenceEqual(order);
        }
        // 保留的脚本查询过公开图层表时，按原作声明顺序逐位重排：没保留的对象留同 id、同名、不绘制的占位，
        // 每个视频组占用组内一个成员（或它在组父级下的祖先）的位置、id 和名字：优先没保留的；只剩因查找被保留的成员时，
        // 视频顶替它，但它下面不能有实时对象或别的视频组的父级（否则它们改继承视频的变换）；只因查找而保留、不绘制的子对象照旧挂在同 id 下。
        // 视频放在成员的源位置，只要求实时层之间的先后与计划相同。找不到槽位就不重排，交给下面的检查。
        // 原位模式（覆盖层越过了组内后续成员，见 Composer）例外：视频必须占组内最早成员的槽位，视频与实时层的先后都要与计划相同。
        bool inPlace = plan["occlusion_tradeoff"]?["in_place"] is not null;
        var moved = new Dictionary<int, int>();
        bool HasLiveDescendant(int id) => originalObjects.Any(pair => Int(pair.Value["parent"]) == id &&
            (liveIds.Contains(pair.Key) || videos.Any(v => Int(v.Group["parent_id"]) == pair.Key) || HasLiveDescendant(pair.Key)));
        JsonArray? PublicLayerTable()
        {
            var kept = finalObjects.OfType<JsonObject>().Where(obj => videos.All(v => v.Video != obj)).ToDictionary(Id);
            var bySlot = new Dictionary<int, JsonObject>();
            foreach (var (video, group) in videos)
            {
                int? groupParent = Int(group["parent_id"]);
                int? SlotOf(int id)
                {
                    while (originalObjects.TryGetValue(id, out var obj))
                    {
                        int? up = Int(obj["parent"]) is int p && originalObjects.ContainsKey(p) ? p : null;
                        if (up == groupParent) return id;
                        if (up is not int next) return null;
                        id = next;
                    }
                    return null;
                }
                var members = group["layer_ids"]!.AsArray().Select(n => Int(n)).OfType<int>().ToHashSet();
                var slots = sourceDrawOrder.Where(members.Contains).Select(SlotOf).OfType<int>()
                    .Where(id => !bySlot.ContainsKey(id) && !HasLiveDescendant(id));
                if ((inPlace ? slots : slots.OrderBy(kept.ContainsKey)).Cast<int?>().FirstOrDefault() is not int free) return null;
                var placed = video.DeepClone().AsObject();
                placed["id"] = free;
                placed["name"] = originalObjects[free]["name"]?.DeepClone();
                kept.Remove(free);
                bySlot[free] = placed;
                moved[Id(video)] = free;
            }
            var table = new JsonArray();
            foreach (var (id, original) in originalObjects)
                table.Add(bySlot.GetValueOrDefault(id) ?? kept.GetValueOrDefault(id)?.DeepClone() ??
                    new JsonObject { ["id"] = id, ["name"] = original["name"]?.DeepClone() });
            return table;
        }
        if (PublicLayerQueries(finalObjects.OfType<JsonObject>(), dependencies).Any() && PublicLayerTable() is JsonArray table)
        {
            var order = inPlace ? expected.Select(id => moved.GetValueOrDefault(id, id)).ToList() : expected.Where(id => !moved.ContainsKey(id)).ToList();
            if (KeepsDrawOrder(table, order)) (finalObjects, expected) = (table, order);
        }
        if (!KeepsDrawOrder(finalObjects, expected))
            throw new Blocker(BlockerCode.HierarchyChangesDrawOrder).ToException();
        GuardPublicLayerQueries(originalObjects.Values, finalObjects.OfType<JsonObject>(), dependencies);
        return finalObjects;
    }

    /// <summary>运行时依赖记录去重合并（按 JSON 文本），保持先后顺序。</summary>
    internal static JsonArray MergeRuntimeDependencies(JsonArray first, JsonArray second)
    {
        var merged = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in first.Concat(second))
            if (dependency is not null && seen.Add(dependency.ToJsonString())) merged.Add(dependency.DeepClone());
        return merged;
    }

    /// <summary>保留下来、带脚本的对象在运行时查询公开图层表（数量、下标、枚举、顺序）的记录。</summary>
    private static IEnumerable<JsonObject> PublicLayerQueries(IEnumerable<JsonObject> finalObjects, JsonArray dependencies)
    {
        var finalById = finalObjects.ToDictionary(Id);
        return dependencies.OfType<JsonObject>().Where(dependency =>
            dependency["operation"]?.GetValue<string>() == "query" && dependency["property"]?.GetValue<string>() is
                "layer_numeric_index" or "layer_count" or "layer_enumeration" or "layer_index" or "layer_order" &&
            Int(dependency["owner"]) is int id && finalById.TryGetValue(id, out var owner) &&
            SceneAnalyzer.Walk(owner).OfType<JsonObject>().Any(node => node["script"] is JsonValue));
    }

    /// <summary>
    /// 保留下来的脚本查询过公开图层表时，成品对象表的数量/顺序必须与原作相同（按分配装配时已逐位重排）。
    /// 成对比较（CandidateValidation）用同一条检查核对两边的运行时查询。
    /// </summary>
    internal static void GuardPublicLayerQueries(IEnumerable<JsonObject> originalObjects,
        IEnumerable<JsonObject> finalObjects, JsonArray dependencies)
    {
        var original = originalObjects.Select(Id).ToArray();
        var final = finalObjects.Select(Id).ToArray();
        foreach (var query in PublicLayerQueries(finalObjects, dependencies))
        {
            string property = query["property"]!.GetValue<string>();
            if (property == "layer_count" ? original.Length != final.Length : !original.SequenceEqual(final))
                throw new Blocker(BlockerCode.PublicLayerQuery,
                    [property, property == "layer_count" ? "count" : "order"], [property, property == "layer_count" ? "数量" : "顺序"]).ToException();
        }
    }
}
