using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 场景层级：作者顺序的对象表、父子关系与每个对象所属的根。分析阶段的层级只从这里取，不再在各处现拼 objects/rootOf。
/// 父指向场景外的对象按"没有父"处理；父链成环直接拒绝。
/// </summary>
internal sealed class SceneGraph
{
    /// <summary>对象表，键为对象 id，枚举顺序即 scene.json 里 objects 的作者顺序。</summary>
    internal Dictionary<int, JsonObject> Objects { get; }
    /// <summary>作者顺序的全部对象 id。</summary>
    internal int[] SourceOrder { get; }
    /// <summary>每个对象沿父链走到的最上层对象。</summary>
    internal Dictionary<int, int> RootOf { get; }
    /// <summary>作者顺序的根对象（自己就是自己的根）。</summary>
    internal int[] Roots { get; }

    internal SceneGraph(JsonObject scene)
    {
        Objects = scene["objects"]!.AsArray().OfType<JsonObject>().ToDictionary(HybridScenePlanner.Id);
        SourceOrder = Objects.Keys.ToArray();
        RootOf = Objects.Keys.ToDictionary(id => id, RootFor);
        Roots = SourceOrder.Where(id => RootOf[id] == id).ToArray();
    }

    private int RootFor(int id)
    {
        var seen = new HashSet<int>();
        // 父不在场景里按没有父处理。
        while (Objects.TryGetValue(id, out var item) && HybridScenePlanner.Int(item["parent"]) is int parent && Objects.ContainsKey(parent))
        {
            if (!seen.Add(id)) throw new InvalidDataException("Scene parent cycle.");
            id = parent;
        }
        return id;
    }
}
