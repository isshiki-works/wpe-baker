using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>Versioned allocation semantics; request and result versions are independent.</summary>
public static class HybridPlanFormat
{
    public const int CurrentVersion = 3;

    public static void Validate(JsonObject plan)
    {
        int version = Number(plan["schema_version"], "schema_version");
        if (version is not (2 or CurrentVersion) || plan["kind"]?.GetValue<string>() != "hybrid_video")
            throw new InvalidDataException("A supported Scene plan (version 2 or 3) is required; analyze the source with this version of WPE Baker.");
        foreach (var group in Objects(plan["video_groups"], "video_groups"))
            if (group["id"] is not null && ProjectSource.NormalizeResource(Text(group["id"], "video group id")).Contains('/'))
                throw new InvalidDataException("Video group IDs must be single path components.");
        if (version == 2)
        {
            if ((plan["layers"] as JsonArray)?.OfType<JsonObject>().Any(layer =>
                    layer["allocation_root"] is not null && Number(layer["allocation_root"], "allocation_root") != Number(layer["root"], "root")) == true ||
                (plan["video_groups"] as JsonArray)?.OfType<JsonObject>().Any(group => group["parent_id"] is not null || group["parent_transform"] is not null) == true)
                throw new InvalidDataException("Subtree allocation requires plan version 3; a version 2 plan must retain original root semantics.");
            return;
        }
        var layers = Objects(plan["layers"], "layers");
        var byId = new Dictionary<int, JsonObject>();
        foreach (var layer in layers)
        {
            int id = Number(layer["id"], "layer id");
            if (!layer.ContainsKey("parent") || !layer.ContainsKey("allocation_root") || !byId.TryAdd(id, layer))
                throw new InvalidDataException("Version 3 layers require unique IDs, author parents and allocation roots.");
        }
        int? Parent(JsonObject layer)
        {
            int? parent = layer["parent"] is null ? null : Number(layer["parent"], "parent");
            return parent is int id && byId.ContainsKey(id) ? id : null;
        }
        var settled = new HashSet<int>();
        foreach (var (id, layer) in byId)
        {
            int author = Number(layer["root"], "root"), allocation = Number(layer["allocation_root"], "allocation_root");
            if (!byId.ContainsKey(author) || !byId.TryGetValue(allocation, out var unit) || Number(unit["allocation_root"], "allocation_root") != allocation)
                throw new InvalidDataException("Plan references an unknown author or allocation root.");
            int? parent = Parent(layer);
            bool hasParent = parent is int p && byId.ContainsKey(p);
            if ((hasParent && Number(byId[parent!.Value]["root"], "root") != author) || (!hasParent && author != id) ||
                (allocation != id && (!hasParent || Number(byId[parent!.Value]["allocation_root"], "allocation_root") != allocation)))
                throw new InvalidDataException("Plan author and allocation roots do not match its parent tree.");
            var path = new HashSet<int>();
            int current = id;
            while (!settled.Contains(current))
            {
                if (!path.Add(current)) throw new InvalidDataException("Plan parent hierarchy contains a cycle.");
                int? next = Parent(byId[current]);
                if (next is not int n || !byId.ContainsKey(n)) break;
                current = n;
            }
            settled.UnionWith(path);
        }
        var allocationOrder = Ids(plan["root_order"], "root_order");
        var authorOrder = Ids(plan["source_root_order"], "source_root_order");
        if (!allocationOrder.ToHashSet().SetEquals(layers.Select(layer => Number(layer["allocation_root"], "allocation_root"))) ||
            !authorOrder.ToHashSet().SetEquals(layers.Select(layer => Number(layer["root"], "root"))))
            throw new InvalidDataException("Plan root orders must cover their respective author and allocation roots.");
        var groups = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var groupedRoots = new HashSet<int>();
        foreach (var group in Objects(plan["video_groups"], "video_groups"))
        {
            if (!group.ContainsKey("parent_id") || !group.ContainsKey("parent_transform"))
                throw new InvalidDataException("Version 3 video groups require explicit parent placement metadata.");
            string id = Text(group["id"], "video group id");
            int? parent = group["parent_id"] is null ? null : Number(group["parent_id"], "parent_id");
            var roots = Ids(group["root_ids"], "root_ids").ToHashSet();
            if (roots.Count == 0 || roots.Any(unitId => !byId.ContainsKey(unitId) || !allocationOrder.Contains(unitId) || Parent(byId[unitId]) != parent) ||
                Ids(group["layer_ids"], "layer_ids").Any(layerId => !byId.ContainsKey(layerId) || !roots.Contains(Number(byId[layerId]["allocation_root"], "allocation_root"))) ||
                !groups.TryAdd(id, roots) || roots.Overlaps(groupedRoots))
                throw new InvalidDataException("Video group members do not match their allocation roots and shared parent.");
            groupedRoots.UnionWith(roots);
            if (parent is int parentId)
            {
                if (!byId.ContainsKey(parentId) || group["parent_transform"] is not JsonObject transform)
                    throw new InvalidDataException("Parented video groups require a known parent and capture transform.");
                Vector(transform["origin"], false);
                Vector(transform["scale"], true);
            }
            else if (group["parent_transform"] is not null) throw new InvalidDataException("Root video groups cannot carry a parent transform.");
        }
        var composedGroups = new HashSet<string>(StringComparer.Ordinal);
        var liveRoots = new HashSet<int>();
        foreach (var entry in Objects(plan["composition"], "composition"))
        {
            bool video = entry.ContainsKey("video_group"), live = entry.ContainsKey("live_root");
            if (video == live) throw new InvalidDataException("Each composition entry must name one video group or live allocation root.");
            if (video)
            {
                string id = Text(entry["video_group"], "composition video group");
                if (!groups.ContainsKey(id) || !composedGroups.Add(id))
                    throw new InvalidDataException("Composition repeats or references an unknown video group.");
            }
            else
            {
                int root = Number(entry["live_root"], "composition live root");
                if (!allocationOrder.Contains(root) || !liveRoots.Add(root))
                    throw new InvalidDataException("Composition repeats or references an unknown live allocation root.");
            }
        }
        if (!composedGroups.SetEquals(groups.Keys) || groupedRoots.Overlaps(liveRoots))
            throw new InvalidDataException("Composition must place every video group exactly once without double-writing an allocation root.");
    }

    private static int Number(JsonNode? node, string name) => node is JsonValue value && value.TryGetValue<int>(out int number)
        ? number : throw new InvalidDataException($"Invalid plan {name}.");
    private static string Text(JsonNode? node, string name) => node is JsonValue value && value.TryGetValue<string>(out string? text) && !string.IsNullOrWhiteSpace(text)
        ? text : throw new InvalidDataException($"Invalid plan {name}.");
    private static JsonObject[] Objects(JsonNode? node, string name) => node is JsonArray array && array.All(item => item is JsonObject)
        ? array.OfType<JsonObject>().ToArray() : throw new InvalidDataException($"Invalid plan {name}.");
    private static int[] Ids(JsonNode? node, string name)
    {
        if (node is not JsonArray array) throw new InvalidDataException($"Invalid plan {name}.");
        int[] ids = array.Select(item => Number(item, name)).ToArray();
        if (ids.Distinct().Count() != ids.Length) throw new InvalidDataException($"Duplicate plan {name}.");
        return ids;
    }
    private static void Vector(JsonNode? node, bool nonzero)
    {
        if (node is not JsonArray { Count: 3 } values || values.Any(item => item is not JsonValue v ||
            !v.TryGetValue<double>(out double n) || !double.IsFinite(n) || (nonzero && n == 0)))
            throw new InvalidDataException("Invalid static parent transform in plan.");
    }
}
