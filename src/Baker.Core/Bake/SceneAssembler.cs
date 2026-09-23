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
        foreach (var entry in plan["composition"]!.AsArray().OfType<JsonObject>())
        {
            if (entry["video_group"] is JsonValue groupName && replacements.TryGetValue(groupName.GetValue<string>(), out var replacement))
            {
                var group = plan["video_groups"]!.AsArray().OfType<JsonObject>().Single(g => g["id"]!.GetValue<string>() == groupName.GetValue<string>());
                if (Int(replacement["parent"]) != Int(group["parent_id"]))
                    throw new Blocker(BlockerCode.ReplacementParentMismatch).ToException();
                if (Int(replacement["parent"]) is int parent) Emit(parent);
                finalObjects.Add(replacement.DeepClone());
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
        var all = finalObjects.OfType<JsonObject>().ToArray();
        var ids = all.Select(Id).ToHashSet();
        var expectedIds = expected.ToHashSet();
        var actual = new List<int>();
        void Visit(int id)
        {
            if (expectedIds.Contains(id)) actual.Add(id);
            foreach (var child in all.Where(obj => Int(obj["parent"]) == id)) Visit(Id(child));
        }
        foreach (var obj in all.Where(obj => Int(obj["parent"]) is not int parent || !ids.Contains(parent)))
            Visit(Id(obj));
        if (!actual.SequenceEqual(expected))
            throw new Blocker(BlockerCode.HierarchyChangesDrawOrder).ToException();
        GuardPublicLayerQueries(originalObjects.Values, all, dependencies);
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

    /// <summary>
    /// 保留下来的脚本查询过图层数量或顺序时，成品对象表的数量/顺序必须与原作相同；
    /// 唯一放行的例外是脚本在 init 里按自己的位置插入新图层（<see cref="IsSelfAnchoredInsert"/>）。
    /// 成对比较（CandidateValidation）用同一条检查核对两边的运行时查询。
    /// </summary>
    internal static void GuardPublicLayerQueries(IEnumerable<JsonObject> originalObjects,
        IEnumerable<JsonObject> finalObjects, JsonArray dependencies)
    {
        var original = originalObjects.Select(Id).ToArray();
        var final = finalObjects.ToArray();
        var finalById = final.ToDictionary(Id);
        foreach (var query in dependencies.OfType<JsonObject>().Where(dependency =>
            dependency["operation"]?.GetValue<string>() == "query" && dependency["property"]?.GetValue<string>() is
                "layer_numeric_index" or "layer_count" or "layer_enumeration" or "layer_index" or "layer_order"))
        {
            int? owner = Int(query["owner"]);
            if (owner is not int id || !finalById.TryGetValue(id, out var objectWithScript) ||
                !SceneAnalyzer.Walk(objectWithScript).OfType<JsonObject>().Any(node => node["script"] is JsonValue)) continue;
            string property = query["property"]!.GetValue<string>();
            if ((property == "layer_count" ? original.Length != final.Length : !original.SequenceEqual(final.Select(Id))) &&
                !SelfAnchoredInsert(originalObjects, finalById, query, property))
                throw new Blocker(BlockerCode.PublicLayerQuery,
                    [property, property == "layer_count" ? "count" : "order"], [property, property == "layer_count" ? "数量" : "顺序"]).ToException();
        }
    }

    private static bool SelfAnchoredInsert(IEnumerable<JsonObject> originalObjects, IReadOnlyDictionary<int, JsonObject> final,
        JsonObject query, string property)
    {
        if (property is not ("layer_index" or "layer_order") || query["initialization"]?.GetValue<bool>() != true ||
            Int(query["owner"]) is not int owner || query["binding"]?.GetValue<string>() is not string binding) return false;
        JsonObject? source = originalObjects.FirstOrDefault(obj => Id(obj) == owner);
        if (source is null || !final.TryGetValue(owner, out var candidate) || Int(source["parent"]) != Int(candidate["parent"])) return false;
        string? sourceScript = source[binding]?["script"]?.GetValue<string>();
        return sourceScript is not null && sourceScript == candidate[binding]?["script"]?.GetValue<string>() && IsSelfAnchoredInsert(sourceScript);
    }

    private static bool IsSelfAnchoredInsert(string code)
    {
        var tokens = new List<string>();
        for (int i = 0; i < code.Length;)
        {
            char c = code[i]; if (char.IsWhiteSpace(c)) { ++i; continue; } if (c == '`') return false;
            if (c is '\'' or '"') { char quote = c; ++i; while (i < code.Length && code[i] != quote) { if (code[i++] == '\\' && i < code.Length) ++i; } if (i == code.Length) return false; ++i; tokens.Add("$literal"); continue; }
            if (c == '/' && i + 1 < code.Length && (code[i + 1] == '/' || code[i + 1] == '*')) { bool line = code[i + 1] == '/'; i += 2; if (line) while (i < code.Length && code[i] is not '\r' and not '\n') ++i; else { while (i + 1 < code.Length && (code[i] != '*' || code[i + 1] != '/')) ++i; if (i + 1 >= code.Length) return false; i += 2; } continue; }
            if (c == '/' && (tokens.Count == 0 || tokens[^1] is "=" or "(" or "[" or "{" or "," or ":" or ";" or "!")) { ++i; while (i < code.Length && code[i] != '/') { if (code[i++] == '\\' && i < code.Length) ++i; } if (i == code.Length) return false; ++i; while (i < code.Length && char.IsLetter(code[i])) ++i; tokens.Add("$literal"); continue; }
            if (char.IsLetter(c) || c is '_' or '$') { int start = i++; while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] is '_' or '$')) ++i; tokens.Add(code[start..i]); continue; }
            if (char.IsDigit(c)) { int start = i++; while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] == '.')) ++i; tokens.Add(code[start..i]); continue; }
            if (c == '=' && i + 1 < code.Length && code[i + 1] == '>') { tokens.Add("=>"); i += 2; continue; }
            if ("(){};,.=<>+-*![]:?/".Contains(c)) { tokens.Add(c.ToString()); ++i; continue; } return false;
        }
        bool At(int at, params string[] expected) => at + expected.Length <= tokens.Count && expected.Select((value, offset) => tokens[at + offset] == value).All(match => match);
        var forbidden = new HashSet<string>(StringComparer.Ordinal) { "eval", "Function", "Reflect", "Proxy", "with", "globalThis", "constructor", "__proto__", "prototype", "import" }; if (tokens.Any(forbidden.Contains)) return false;
        int[] depth = new int[tokens.Count]; int level = 0; for (int i = 0; i < tokens.Count; ++i) { depth[i] = level; if (tokens[i] == "{") ++level; else if (tokens[i] == "}" && --level < 0) return false; } if (level != 0) return false;
        int Match(int open, string left, string right) { int nesting = 0; for (int i = open; i < tokens.Count; ++i) { if (tokens[i] == left) ++nesting; else if (tokens[i] == right && --nesting == 0) return i; } return -1; }
        int init = -1, initBody = -1, initEnd = -1; for (int i = 0; i < tokens.Count; ++i) { if (!At(i, "function", "init", "(")) continue; int close = Match(i + 2, "(", ")"); if (close != i + 3 || close + 1 >= tokens.Count || tokens[close + 1] != "{" || init >= 0) return false; init = i; initBody = close + 2; initEnd = Match(close + 1, "{", "}"); if (initEnd < 0) return false; } if (init < 0) return false;
        int anchor = -1, bar = -1, create = -1, sort = -1; for (int i = initBody; i < initEnd; ++i) { if (tokens[i] is "function" or "async" or "=>" or "setTimeout" or "setInterval") return false; if (At(i, "let") && i + 9 < initEnd && depth[i] == depth[initBody] && At(i + 2, "=", "thisScene", ".", "getLayerIndex", "(", "thisLayer", ")", ";")) { if (anchor >= 0) return false; anchor = i + 1; } if (At(i, "let") && i + 9 < initEnd && At(i + 2, "=", "thisScene", ".", "createLayer", "(", "$literal", ")", ";")) { if (bar >= 0) return false; bar = i + 1; create = i; } if (At(i, "thisScene", ".", "sortLayer", "(")) { if (sort >= 0) return false; sort = i; } }
        if (anchor < 0 || bar < 0 || sort < 0 || anchor >= create || create >= sort || depth[create] != depth[sort] || !At(sort, "thisScene", ".", "sortLayer", "(", tokens[bar], ",", tokens[anchor], ")", ";")) return false;
        for (int i = 0; i < tokens.Count; ++i) { if (tokens[i] == "thisScene" && i != anchor + 2 && i != create + 3 && i != sort) return false; if (tokens[i] == "thisLayer" && i + 1 < tokens.Count && tokens[i + 1] == "=") return false; if (i + 1 < tokens.Count && (tokens[i] is "let" or "const" or "var") && tokens[i + 1] == "thisLayer") return false; if (tokens[i] == tokens[anchor] && i != anchor && i != sort + 6) return false; }
        for (int i = create + 10; i < sort;) { if (!At(i, tokens[bar], ".") || i + 3 >= sort || tokens[i + 3] != "=") return false; int start = i; while (i < sort && tokens[i] != ";") { if (i > start && tokens[i] == tokens[bar]) return false; ++i; } if (i++ >= sort) return false; }
        return true;
    }
}
