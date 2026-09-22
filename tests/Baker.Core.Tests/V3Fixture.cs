using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// 测试夹具：把只写了 id/root 的扁平图层表补成 plan v3 形态（每个作者根一个分配单元，子层直接挂在根下，
/// 组按成员推出 root_ids，合成顺序为各组再接其余实时根，未进组的层记为实时）。已有的 v3 字段不动，composition 总是按组重建。
/// </summary>
internal static class V3Fixture
{
    internal static JsonObject Upgrade(JsonObject plan)
    {
        plan["schema_version"] = HybridPlanFormat.CurrentVersion;
        JsonObject[] layers = plan["layers"]!.AsArray().OfType<JsonObject>().ToArray();
        foreach (JsonObject layer in layers)
        {
            int id = layer["id"]!.GetValue<int>(), root = layer["root"]!.GetValue<int>();
            if (!layer.ContainsKey("parent")) layer["parent"] = id == root ? null : JsonValue.Create(root);
            if (!layer.ContainsKey("allocation_root")) layer["allocation_root"] = root;
        }
        int Unit(int layerId) => layers.Single(layer => layer["id"]!.GetValue<int>() == layerId)["allocation_root"]!.GetValue<int>();
        int[] units = [.. layers.Select(layer => layer["allocation_root"]!.GetValue<int>()).Distinct()];
        plan["root_order"] ??= new JsonArray([.. units.Select(id => (JsonNode)JsonValue.Create(id))]);
        plan["source_root_order"] ??= new JsonArray([.. layers.Select(layer => layer["root"]!.GetValue<int>()).Distinct().Select(id => (JsonNode)JsonValue.Create(id))]);
        var composition = new JsonArray();
        var grouped = new HashSet<int>();
        int index = 0;
        foreach (JsonObject group in plan["video_groups"]!.AsArray().OfType<JsonObject>())
        {
            ++index;
            group["id"] ??= "group-" + index;
            if (!group.ContainsKey("root_ids"))
                group["root_ids"] = new JsonArray([.. group["layer_ids"]!.AsArray().Select(id => Unit(id!.GetValue<int>())).Distinct()
                    .Select(id => (JsonNode)JsonValue.Create(id))]);
            if (!group.ContainsKey("parent_id")) group["parent_id"] = null;
            if (!group.ContainsKey("parent_transform")) group["parent_transform"] = null;
            grouped.UnionWith(group["root_ids"]!.AsArray().Select(id => id!.GetValue<int>()));
            composition.Add(new JsonObject { ["video_group"] = group["id"]!.GetValue<string>() });
        }
        foreach (int unit in plan["root_order"]!.AsArray().Select(id => id!.GetValue<int>()).Where(unit => !grouped.Contains(unit)))
            composition.Add(new JsonObject { ["live_root"] = unit });
        plan["composition"] = composition;
        plan["live_layer_ids"] ??= new JsonArray([.. layers.Where(layer => !grouped.Contains(layer["allocation_root"]!.GetValue<int>()))
            .Select(layer => (JsonNode)JsonValue.Create(layer["id"]!.GetValue<int>()))]);
        return plan;
    }
}
