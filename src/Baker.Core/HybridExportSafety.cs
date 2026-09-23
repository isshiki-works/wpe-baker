using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 组整段捕获后的迟到依赖判定：外部输入或跨边界写入落在烘焙组及其保留祖先上、源脚本报错落在烘焙内容里时拒绝。
/// 装配与公开图层查询检查在 <see cref="SceneAssembler"/>。
/// </summary>
internal static class HybridExportSafety
{
    internal static JsonObject ClassifyLateSourceScriptErrors(JsonArray errors, IReadOnlySet<int> groupLayers,
        IReadOnlySet<int> liveLayerIds, IReadOnlyDictionary<int, JsonObject> sourceObjects)
    {
        var protectedLayers = ProtectedLayers(groupLayers, sourceObjects);
        var unsafeErrors = new JsonArray();
        var externalLiveErrors = new JsonArray();
        foreach (var error in errors.OfType<JsonObject>())
        {
            if (HybridScenePlanner.Int(error["owner_layer_id"]) is not int owner || !sourceObjects.ContainsKey(owner))
                throw new InvalidDataException("A full capture source script fault lacks a known authored owner.");
            string? reason = groupLayers.Contains(owner) ? "source_script_fault_in_baked_layer" :
                protectedLayers.Contains(owner) ? "source_script_fault_in_retained_ancestor" :
                !liveLayerIds.Contains(owner) ? "source_script_fault_in_unretained_layer" : null;
            var copy = error.DeepClone().AsObject();
            if (reason is null) externalLiveErrors.Add(copy);
            else { copy["rejection_reason"] = reason; unsafeErrors.Add(copy); }
        }
        return new JsonObject { ["unsafe"] = unsafeErrors, ["external_live"] = externalLiveErrors };
    }

    internal static JsonArray LateExternalDependencies(JsonObject master, IReadOnlySet<int> groupLayers,
        IReadOnlyDictionary<int, JsonObject> sourceObjects)
    {
        if (master["native_result"]?["runtime_dependencies"] is not JsonArray dependencies)
            throw new InvalidDataException("A full group capture did not return the requested runtime dependency trace.");
        var protectedLayers = ProtectedLayers(groupLayers, sourceObjects);
        var unsafeDependencies = new JsonArray();
        foreach (var dependency in dependencies.OfType<JsonObject>())
        {
            int? owner = HybridScenePlanner.Int(dependency["owner"]), target = HybridScenePlanner.Int(dependency["target"]);
            string? operation = dependency["operation"]?.GetValue<string>();
            string? reason = null;
            if (operation == "input" && owner is int inputOwner && protectedLayers.Contains(inputOwner) &&
                dependency["property"]?.GetValue<string>() is "wall_clock" or "audio" or "pointer" or "media")
                reason = groupLayers.Contains(inputOwner) ? "external_input_to_baked_layer" : "external_input_to_retained_ancestor";
            if (operation == "write" && dependency["initialization"]?.GetValue<bool>() != true && target is int written)
            {
                bool groupWriter = owner is int writer && groupLayers.Contains(writer);
                if (!groupWriter && protectedLayers.Contains(written))
                    reason = groupLayers.Contains(written) ? "external_controller_write_to_baked_layer" : "external_controller_write_to_retained_ancestor";
                else if (groupWriter && !groupLayers.Contains(written) && sourceObjects.ContainsKey(written))
                    reason = protectedLayers.Contains(written) ? "baked_controller_write_to_retained_ancestor" : "baked_controller_write_to_external_layer";
            }
            if (reason is null) continue;
            var rejected = dependency.DeepClone().AsObject();
            rejected["rejection_reason"] = reason;
            unsafeDependencies.Add(rejected);
        }
        return unsafeDependencies;
    }

    private static HashSet<int> ProtectedLayers(IReadOnlySet<int> groupLayers,
        IReadOnlyDictionary<int, JsonObject> sourceObjects)
    {
        var protectedLayers = groupLayers.ToHashSet();
        foreach (int layer in groupLayers)
        {
            int id = layer;
            var seen = new HashSet<int>();
            while (sourceObjects.TryGetValue(id, out var obj) && HybridScenePlanner.Int(obj["parent"]) is int parent && sourceObjects.ContainsKey(parent))
            {
                if (!seen.Add(id)) throw new InvalidDataException("Scene parent cycle during capture dependency validation.");
                protectedLayers.Add(parent);
                id = parent;
            }
        }
        return protectedLayers;
    }
}
