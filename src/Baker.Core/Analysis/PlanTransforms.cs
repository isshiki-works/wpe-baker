using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Baker.Core.SceneGraph;

namespace Baker.Core;

/// <summary>
/// 按 plan 改写场景与 plan 本身的变换：bake 前刷新循环、前景分配、冻结时间属性、快照剔除、叠加层上移、文字效果与音频效果取舍。
/// 分析（<see cref="HybridScenePlanner"/>、<see cref="RuntimeObservation"/>、<see cref="LayoutAdmission"/>）与 bake 共用同一份；
/// bake 侧的旧调用经 HybridScenePlanner 的同名转发器进来（登记在 forwarders.txt，C3 删）。
/// </summary>
internal static class PlanTransforms
{
    /// <summary>
    /// 按当前源码与运行时证据重新求解 plan 的循环，覆盖 plan["loop"]：bake 不接受计划里存着的周期与起点。
    /// 走 <see cref="HybridScenePlanner.AnalyzeLoopForProfile"/> 这一个入口，与 analyze、布局降级重算同一口径——
    /// 否则质量档的双上限取优只在 analyze 侧生效，bake 会按另一个上限重算出别的循环。
    /// </summary>
    internal static void RefreshLoop(JsonObject plan, ProjectSource source, JsonObject runtime, HybridAnalyzeRequest settings)
    {
        // 每次求解都重新读一份场景：质量档要在两个上限下各求一次，求解会往场景副本上写，不能共用同一份。
        JsonObject LoopScene()
        {
            var scene = source.ReadJson(source.SceneResource);
            ApplyAudioEffectChoice(scene, plan);
            FreezeTemporalProperties(scene, plan["snapshot_properties"]!.AsObject());
            DaytimeSplit.ApplyState(scene, plan, forAnalysis: true);
            return scene;
        }
        plan["loop"] = HybridScenePlanner.AnalyzeLoopForProfile(LoopScene, source, settings.Assets, runtime,
            plan["video_groups"]!.AsArray().OfType<JsonObject>().SelectMany(g => g["layer_ids"]!.AsArray().Select(n => n!.GetValue<int>())).ToArray(),
            settings, plan["projection"] as JsonObject ?? new JsonObject(), plan["video_groups"] as JsonArray);
        HybridScenePlanner.AnnotateLoopCandidates(plan["loop"]!.AsObject());
        // bake 侧重算的 loop 直接进 bake.json 的 plan 副本，不再 Attach，临时字段当场去掉。
        PlanNarrative.StripTransient(plan["loop"]);
    }

    /// <summary>Returns a plan clone with one selected foreground suffix retained as whole live roots.</summary>
    internal static JsonObject ApplyAllocation(JsonObject plan, IReadOnlyCollection<int> extraLiveRoots,
        JsonArray runtimeDependencies)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(extraLiveRoots);
        ArgumentNullException.ThrowIfNull(runtimeDependencies);
        HybridPlanFormat.Validate(plan);
        var result = plan.DeepClone().AsObject();
        FullFrameDemotion.ResetAdmissionRecords(result);
        var layers = result["layers"]?.AsArray().OfType<JsonObject>().ToArray()
            ?? throw new InvalidDataException("Hybrid plan layers are missing.");
        var layerRoot = new Dictionary<int, int>();
        var rootLayers = new Dictionary<int, List<int>>();
        foreach (var layer in layers)
        {
            int id = Int(layer["id"]) ?? throw new InvalidDataException("Hybrid plan layer id is invalid.");
            int root = Int(layer["allocation_root"] ?? layer["root"]) ?? throw new InvalidDataException("Hybrid plan layer root is invalid.");
            if (!layerRoot.TryAdd(id, root)) throw new InvalidDataException("Hybrid plan contains duplicate layer ids.");
            if (!rootLayers.TryGetValue(root, out var ids)) rootLayers[root] = ids = [];
            ids.Add(id);
        }
        var omittedLayerIds = result["omitted_snapshot_layer_ids"]?.AsArray()
            .Select(node => node?.GetValue<int>() ?? throw new InvalidDataException("Omitted snapshot layer id is invalid."))
            .ToHashSet() ?? [];
        if (omittedLayerIds.Any(id => !layerRoot.ContainsKey(id)))
            throw new InvalidDataException("Omitted snapshot layer is missing from the plan layers.");
        int[] rootOrder = result["root_order"] is JsonArray recordedOrder
            ? recordedOrder.Select(node => node?.GetValue<int>() ?? throw new InvalidDataException("Hybrid root order is invalid.")).ToArray()
            : layers.Where(layer => Int(layer["id"]) == Int(layer["allocation_root"] ?? layer["root"]))
                .Select(layer => layer["id"]!.GetValue<int>()).ToArray();
        if (rootOrder.Distinct().Count() != rootOrder.Length || rootOrder.Any(root => !rootLayers.ContainsKey(root)) ||
            rootLayers.Keys.Any(root => !rootOrder.Contains(root)))
            throw new InvalidDataException("Hybrid root order does not match the plan layers.");
        var rootIndex = rootOrder.Select((root, index) => (root, index)).ToDictionary(item => item.root, item => item.index);
        var groups = result["video_groups"]?.AsArray().OfType<JsonObject>().ToArray()
            ?? throw new InvalidDataException("Hybrid video groups are missing.");
        var groupRoots = new Dictionary<string, int[]>();
        var videoRoots = new HashSet<int>();
        foreach (var group in groups)
        {
            string id = group["id"]?.GetValue<string>() ?? throw new InvalidDataException("Hybrid video group id is missing.");
            int[] roots = group["root_ids"]?.AsArray().Select(node => node?.GetValue<int>()
                ?? throw new InvalidDataException("Hybrid video group root is invalid.")).ToArray()
                ?? throw new InvalidDataException("Hybrid video group roots are missing.");
            if (!groupRoots.TryAdd(id, roots) || roots.Any(root => !rootIndex.ContainsKey(root) || !videoRoots.Add(root)))
                throw new InvalidDataException("Hybrid video group roots are missing, duplicated, or unknown.");
        }
        var optional = result["optional_realtime_roots"]?.AsArray().Select(node => node?.GetValue<int>()
            ?? throw new InvalidDataException("Optional realtime root is invalid.")).ToHashSet() ?? [];
        int[] requested = extraLiveRoots.Distinct().ToArray();
        if (requested.Length != extraLiveRoots.Count || requested.Any(root => !optional.Contains(root) || !videoRoots.Contains(root)))
            throw new InvalidDataException("Foreground allocation roots must be distinct optional realtime video roots.");

        var existingLiveRoots = layers.Where(layer => layer["live"]?.GetValue<bool>() == true)
            .Select(layer => layerRoot[layer["id"]!.GetValue<int>()]).ToHashSet();
        if (result["composition"] is not JsonArray originalComposition)
            throw new InvalidDataException("Hybrid composition is missing.");
        foreach (var entry in originalComposition.OfType<JsonObject>())
            if (Int(entry["live_root"]) is int root)
            {
                if (!rootIndex.ContainsKey(root)) throw new InvalidDataException("Hybrid composition contains an unknown live root.");
                existingLiveRoots.Add(root);
            }
        int? suffixStart = requested.Length == 0 ? null : requested.Min(root => rootIndex[root]);
        var suffixRoots = suffixStart is int start
            ? rootOrder.Skip(start).Where(videoRoots.Contains).ToHashSet()
            : [];
        var liveRoots = existingLiveRoots.Concat(suffixRoots).ToHashSet();
        var dependencyRoots = new HashSet<int>();
        bool Promote(int layerId, string _)
        {
            if (!layerRoot.TryGetValue(layerId, out int root) || !liveRoots.Add(root)) return false;
            dependencyRoots.Add(root);
            return true;
        }
        // 依赖闭包与分析同一份（按分配单元判）。plan 侧的依赖缺 owner/target/operation 时跳过该条，不抛错（与改调前相同）。
        Liveness.Close(runtimeDependencies.OfType<JsonObject>().Where(dependency =>
                Int(dependency["owner"]) is int && Int(dependency["target"]) is int && dependency["operation"] is not null),
            id => layerRoot.TryGetValue(id, out int root) && liveRoots.Contains(root), Promote, ownerPerRule: false,
            static _ => false, static _ => false);

        var allocation = new JsonObject {
            ["status"] = "applied", ["requested_live_root_ids"] = JsonSerializer.SerializeToNode(requested),
            ["suffix_start_root_id"] = suffixStart is int suffixIndex ? JsonValue.Create(rootOrder[suffixIndex]) : null,
            ["foreground_live_root_ids"] = JsonSerializer.SerializeToNode(rootOrder.Where(suffixRoots.Contains)),
            ["dependency_live_root_ids"] = JsonSerializer.SerializeToNode(rootOrder.Where(dependencyRoots.Contains)),
            ["scope"] = "Whole allocation subtrees only; author parents, existing group order, parallax depths and scene-clear ownership are preserved."
        };
        result["allocation"] = allocation;
        JsonObject RejectAllocation(Blocker blocker, string status = "requires_user_choice")
        {
            string reason = blocker.Text;
            allocation["status"] = "requires_resolution";
            allocation["reason"] = reason;
            PlanBlockers.Add(result, blocker);
            result["status"] = "requires_resolution";
            result["video_layout_admission"] = LayoutAdmission.AdmissionRecord(PlanSettings.Of(result).VideoLayout, status, reason,
                "The allocation did not reorder roots, split a video group or change parallax settings.");
            return result;
        }
        foreach (var group in groups)
        {
            bool removed = false;
            foreach (int root in groupRoots[group["id"]!.GetValue<string>()])
            {
                if (liveRoots.Contains(root)) removed = true;
                else if (removed)
                    return RejectAllocation(new Blocker(BlockerCode.ForegroundSplitsVideoGroup));
            }
        }
        if (dependencyRoots.Any(root => !videoRoots.Contains(root) && !existingLiveRoots.Contains(root)))
            return RejectAllocation(new Blocker(BlockerCode.ForegroundOutsideComposition));

        var remainingGroups = new HashSet<string>(StringComparer.Ordinal);
        var promotedByGroup = new Dictionary<string, int[]>(StringComparer.Ordinal);
        var rebuiltGroups = new JsonArray();
        foreach (var group in groups)
        {
            string id = group["id"]!.GetValue<string>();
            int[] promoted = groupRoots[id].Where(liveRoots.Contains).ToArray();
            int[] remaining = groupRoots[id].Where(root => !liveRoots.Contains(root)).ToArray();
            promotedByGroup[id] = promoted;
            if (remaining.Length == 0) continue;
            var rebuilt = group.DeepClone().AsObject();
            rebuilt["root_ids"] = JsonSerializer.SerializeToNode(remaining);
            rebuilt["layer_ids"] = new JsonArray(group["layer_ids"]!.AsArray()
                .Where(node => !omittedLayerIds.Contains(node!.GetValue<int>()) &&
                    !liveRoots.Contains(layerRoot[node!.GetValue<int>()])).Select(node => node!.DeepClone()).ToArray());
            rebuiltGroups.Add(rebuilt);
            remainingGroups.Add(id);
        }
        result["video_groups"] = rebuiltGroups;
        var rebuiltComposition = new JsonArray();
        var emittedLive = new HashSet<int>();
        foreach (var entry in originalComposition.OfType<JsonObject>())
        {
            if (entry["video_group"] is JsonValue groupValue)
            {
                string id = groupValue.GetValue<string>();
                if (!groupRoots.ContainsKey(id)) throw new InvalidDataException("Hybrid composition references an unknown video group.");
                if (remainingGroups.Contains(id)) rebuiltComposition.Add(entry.DeepClone());
                foreach (int root in promotedByGroup[id])
                    if (emittedLive.Add(root)) rebuiltComposition.Add(new JsonObject { ["live_root"] = root });
            }
            else if (Int(entry["live_root"]) is int root && emittedLive.Add(root)) rebuiltComposition.Add(entry.DeepClone());
        }
        result["composition"] = rebuiltComposition;
        result["live_layer_ids"] = JsonSerializer.SerializeToNode(layers.Select(layer => layer["id"]!.GetValue<int>())
            .Where(id => !omittedLayerIds.Contains(id) && liveRoots.Contains(layerRoot[id])));
        foreach (var layer in layers)
        {
            int id = layer["id"]!.GetValue<int>();
            if (omittedLayerIds.Contains(id)) { layer["live"] = false; continue; }
            int root = layerRoot[id];
            if (!liveRoots.Contains(root)) continue;
            layer["live"] = true;
            if (layer["allocation"] is not null) layer["allocation"] = "live";
            var reasons = layer["reasons"] as JsonArray ?? new JsonArray();
            layer["reasons"] = reasons;
            string reason = dependencyRoots.Contains(root) ? "required_by_foreground_dependency" :
                suffixRoots.Contains(root) ? "retained_as_foreground_suffix" : "";
            if (reason.Length > 0 && !reasons.Any(node => node?.GetValue<string>() == reason)) reasons.Add(reason);
        }
        var groupOfRoot = rebuiltGroups.OfType<JsonObject>().SelectMany(group => group["root_ids"]!.AsArray()
            .Select(root => (Root: root!.GetValue<int>(), Group: group["id"]!.GetValue<string>())))
            .ToDictionary(item => item.Root, item => item.Group);
        var previousRoles = result["root_roles"]?.AsArray().OfType<JsonObject>()
            .Where(role => Int(role["root_id"]) is int).ToDictionary(role => role["root_id"]!.GetValue<int>()) ?? [];
        result["root_order"] = JsonSerializer.SerializeToNode(rootOrder);
        result["root_roles"] = new JsonArray(rootOrder.Select((root, index) => {
            var role = previousRoles.GetValueOrDefault(root)?.DeepClone().AsObject() ?? new JsonObject();
            role["root_id"] = root; role["source_order"] = index;
            role["role"] = dependencyRoots.Contains(root) ? "dependency_live" : suffixRoots.Contains(root) ? "foreground_live" :
                existingLiveRoots.Contains(root) ? "live" : groupOfRoot.ContainsKey(root) ? "video" :
                role["role"]?.GetValue<string>() == "omitted_snapshot" ? "omitted_snapshot" : "inactive";
            role["video_group"] = groupOfRoot.TryGetValue(root, out string? group) ? group : null;
            return (JsonNode)role;
        }).ToArray());

        Blocker? layoutConflict = LayoutAdmission.FullFrameConflict(result) ?? LayoutAdmission.CompositionHierarchyConflict(result);
        if (layoutConflict is not null) return RejectAllocation(layoutConflict, LayoutAdmission.ConflictStatus(result));
        result["video_layout_admission"] = LayoutAdmission.AllowedRecord(PlanSettings.Of(result).VideoLayout);
        return result;
    }

    internal static void FreezeTemporalProperties(JsonObject scene, JsonObject properties)
    {
        void Freeze(JsonObject container, string key)
        {
            if (container[key] is JsonObject binding && binding.ContainsKey("user") && !SceneGraph.Dynamic(binding))
                container[key] = Resolve(binding, properties);
        }
        foreach (var obj in scene["objects"]!.AsArray().OfType<JsonObject>())
        {
            foreach (var effect in obj["effects"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                Freeze(effect, "visible");
                foreach (var pass in effect["passes"]?.AsArray().OfType<JsonObject>() ?? [])
                    foreach (string field in new[] { "constantshadervalues", "combos" })
                        if (pass[field] is JsonObject values)
                            foreach (string key in values.Select(pair => pair.Key).ToArray()) Freeze(values, key);
            }
            foreach (var clip in obj["animationlayers"]?.AsArray().OfType<JsonObject>() ?? []) Freeze(clip, "rate");
        }
    }

    internal static void ApplySnapshotOmissions(JsonObject scene, JsonObject plan)
    {
        if (plan["omitted_snapshot_layer_ids"] is not JsonArray { Count: > 0 } omittedIds) return;
        var omitted = omittedIds.Select(n => n!.GetValue<int>()).ToHashSet();
        var objects = scene["objects"]!.AsArray();
        for (int i = objects.Count - 1; i >= 0; --i)
            if (omitted.Contains(objects[i]!["id"]!.GetValue<int>())) objects.RemoveAt(i);
    }

    internal static void ApplyOverlayPlacement(JsonObject scene, JsonObject plan)
    {
        if (plan["occlusion_tradeoff"]?["status"]?.GetValue<string>() != "applied") return;
        var promoted = plan["occlusion_tradeoff"]!["promoted_roots"]!.AsArray()
            .Select(n => n!["root_id"]!.GetValue<int>()).ToHashSet();
        var objects = scene["objects"]!.AsArray().OfType<JsonObject>().ToArray();
        var byId = objects.ToDictionary(Id);
        var layers = plan["layers"]!.AsArray().OfType<JsonObject>().ToArray();
        // Lift a promoted child declaration to its wholly promoted structural branch. Merely moving
        // the child to the end of the JSON cannot cross a sibling of its ancestor in the native tree.
        var placement = new HashSet<int>();
        foreach (int selected in promoted)
        {
            int id = selected;
            while (Int(byId[id]["parent"]) is int parent && byId.ContainsKey(parent) &&
                layers.Where(layer => SceneGraph.Within(byId, Id(layer), parent) && layer["drawable"]?.GetValue<bool>() == true)
                    .All(layer => promoted.Contains(Int(layer["allocation_root"] ?? layer["root"])!.Value))) id = parent;
            placement.Add(id);
        }
        scene["objects"] = new JsonArray(objects.Where(o => !placement.Contains(Id(o)))
            .Concat(objects.Where(o => placement.Contains(Id(o)))).Select(o => (JsonNode)o.DeepClone()).ToArray());
    }

    internal static void ApplyTextEffectChoice(JsonObject scene, JsonObject plan)
    {
        if (plan["text_effects_choice"]?["status"]?.GetValue<string>() != "applied") return;
        var selected = plan["text_effects_choice"]!["simplified_layers"]!.AsArray().Select(n => n!["id"]!.GetValue<int>()).ToHashSet();
        foreach (var obj in scene["objects"]!.AsArray().OfType<JsonObject>().Where(obj => selected.Contains(Id(obj))))
        {
            if (!obj.ContainsKey("text")) throw new InvalidDataException("Text-effect choice refers to a non-text layer.");
            obj["effects"] = new JsonArray();
        }
    }

    internal static JsonObject DescribeAudioEffectChoice(JsonObject scene, ProjectSource source, string? assets,
        JsonObject properties, JsonObject runtime, string selection)
    {
        if (selection is not "preserve" and not "omit") throw new InvalidDataException("Unknown audio-effect choice.");
        var available = new JsonArray(); var protectedEffects = new JsonArray();
        var objects = scene["objects"]!.AsArray().OfType<JsonObject>().ToArray();
        bool scriptAccess = objects.SelectMany(obj => SceneAnalyzer.Walk(obj).OfType<JsonObject>())
            .Where(n => n["script"] is JsonValue).Select(n => Liveness.CapabilityScanText(n["script"]!.GetValue<string>()))
            .Any(code => Regex.IsMatch(code, @"\b(getEffect|getEffects|findEffect|eval|Function|Reflect|Proxy|import)\b|\.\s*effects\b|\[\s*['""]effects['""]\s*\]"));
        var dependencies = runtime["runtime_dependencies"]!.AsArray().OfType<JsonObject>().ToArray();
        foreach (var obj in objects)
        {
            int id = Id(obj);
            var observed = runtime["runtime_layers"]!.AsArray().OfType<JsonObject>().Where(n => Int(n["owner"]) == id).ToArray();
            var audio = observed.SelectMany(n => n["materials"]?.AsArray().OfType<JsonObject>() ?? [])
                .Where(m => m["uses_audio_spectrum"]?.GetValue<bool>() == true).ToArray();
            if (audio.Length == 0) continue;
            JsonObject Entry(int index, JsonObject? effect, string? reason = null) => new() {
                ["layer_id"] = id, ["layer_name"] = obj["name"]?.DeepClone() ?? JsonValue.Create("Layer"),
                ["effect_index"] = index, ["effect_id"] = effect?["id"]?.DeepClone(), ["file"] = effect?["file"]?.DeepClone(),
                ["name"] = effect?["name"] is JsonValue name && name.TryGetValue<string>(out string? text) && !string.IsNullOrWhiteSpace(text)
                    ? text : effect?["file"]?.GetValue<string>() ?? "Source audio material",
                ["reason"] = reason };
            if (audio.Any(m => m["role"]?.GetValue<string>() != "effect"))
                protectedEffects.Add(Entry(-1, null, "intrinsic_audio_material"));
            if (!audio.Any(m => m["role"]?.GetValue<string>() == "effect")) continue;
            var effects = obj["effects"]?.AsArray().OfType<JsonObject>().ToArray() ?? [];
            var materials = observed.SelectMany(n => n["materials"]?.AsArray().OfType<JsonObject>() ?? [])
                .Where(m => m["role"]?.GetValue<string>() == "effect").ToArray();
            bool mapped = observed.Length == 1 && effects.Length > 0 && effects.Length == materials.Length;
            // ponytail: map only fixed-visible single-pass chains. Stable native effect identities
            // are needed before supporting hidden, multipass or expanded render-node chains.
            for (int index = 0; mapped && index < effects.Length; ++index)
            {
                var effect = effects[index];
                if (effect["visible"] is JsonObject visibility && SceneAnalyzer.Walk(visibility).OfType<JsonObject>().Any(SceneGraph.Dynamic) ||
                    Resolve(effect["visible"], properties)?.ToJsonString() is string shown && shown != "true" ||
                    effect["passes"] is JsonArray { Count: > 1 })
                { mapped = false; break; }
                try
                {
                    string file = effect["file"]?.GetValue<string>() ?? "";
                    JsonObject definition = SceneAnalyzer.ReadResourceJson(source, assets, file);
                    if (definition["passes"] is not JsonArray { Count: 1 } passes || passes[0]?["material"] is not JsonValue materialFile)
                    { mapped = false; break; }
                    JsonObject material = SceneAnalyzer.ReadResourceJson(source, assets, materialFile.GetValue<string>());
                    mapped = material["passes"] is JsonArray { Count: 1 } materialPasses &&
                        materialPasses[0]?["shader"]?.GetValue<string>() == materials[index]["shader"]?.GetValue<string>();
                }
                catch (Exception error) when (error is IOException or InvalidDataException or JsonException)
                { mapped = false; }
            }
            if (!mapped)
            { protectedEffects.Add(Entry(-1, null, "audio_effect_mapping_unavailable")); continue; }
            for (int index = 0; index < effects.Length; ++index)
            {
                if (materials[index]["uses_audio_spectrum"]?.GetValue<bool>() != true) continue;
                var effect = effects[index];
                string? reason = scriptAccess || dependencies.Any(d => Int(d["target"]) == id && d["property"]?.GetValue<string>() == "effect")
                    ? "script_accessed_effect" : SceneAnalyzer.Walk(effect).OfType<JsonObject>().Any(SceneGraph.Dynamic)
                    ? "scripted_or_animated_effect" : materials[index]["active_uniforms"] is JsonArray uniforms &&
                        uniforms.Any(n => n?.GetValue<string>() is "g_PointerPosition" or "g_PointerPositionLast" or "g_ParallaxPosition")
                    ? "other_live_effect_input" : materials[index]["textures"] is JsonArray textures &&
                        textures.Any(n => n?.GetValue<string>() is "_rt_default" or "_rt_FullFrameBuffer")
                    ? "framebuffer_effect" : null;
                if (reason is not null) protectedEffects.Add(Entry(index, effect, reason));
                else available.Add(Entry(index, effect));
            }
        }
        return new JsonObject {
            ["selection"] = selection,
            ["status"] = selection == "omit" && available.Count > 0 ? "applied" : available.Count > 0 ? "available" :
                protectedEffects.Count > 0 ? "protected" : "not_needed",
            ["scope"] = "Explicit appearance tradeoff: remove only the listed fixed single-pass audio effects. Their lighting or other appearance changes no longer respond to music. Other effects, audio scripts, intrinsic audio materials, clocks and pointer interaction remain intact.",
            ["available_effects"] = available, ["omitted_effects"] = selection == "omit" ? available.DeepClone() : new JsonArray(),
            ["protected_effects"] = protectedEffects };
    }

    internal static void ApplyAudioEffectChoice(JsonObject scene, JsonObject plan)
    {
        if (plan["audio_effects_choice"]?["status"]?.GetValue<string>() != "applied") return;
        var objects = scene["objects"]!.AsArray().OfType<JsonObject>().ToDictionary(Id);
        foreach (var layer in plan["audio_effects_choice"]!["omitted_effects"]!.AsArray().OfType<JsonObject>()
            .GroupBy(item => item["layer_id"]!.GetValue<int>()))
        {
            if (!objects.TryGetValue(layer.Key, out var obj) || obj["effects"] is not JsonArray effects)
                throw new InvalidDataException("Audio-effect choice refers to a missing effect layer.");
            foreach (var item in layer.OrderByDescending(item => item["effect_index"]!.GetValue<int>()))
            {
                int index = item["effect_index"]!.GetValue<int>();
                if (index < 0 || index >= effects.Count || effects[index] is not JsonObject effect ||
                    !JsonNode.DeepEquals(effect["id"], item["effect_id"]) || !JsonNode.DeepEquals(effect["file"], item["file"]))
                    throw new InvalidDataException("Audio-effect choice no longer matches its source effect.");
                effects.RemoveAt(index);
            }
        }
    }
}
