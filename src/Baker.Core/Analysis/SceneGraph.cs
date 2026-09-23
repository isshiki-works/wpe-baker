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

    /// <summary><paramref name="id"/> 是否就是 <paramref name="ancestor"/> 或在它的子树里（父指向场景外即止）。</summary>
    internal bool Within(int id, int ancestor)
    {
        while (Objects.TryGetValue(id, out var item))
        {
            if (id == ancestor) return true;
            if (HybridScenePlanner.Int(item["parent"]) is not int parent || !Objects.ContainsKey(parent)) return false;
            id = parent;
        }
        return false;
    }

    /// <summary>绑定节点由动画驱动（animation / animations）。</summary>
    internal static bool Animated(JsonObject node) => node.ContainsKey("animation") || node.ContainsKey("animations");

    /// <summary>绑定节点的值随运行时变（脚本或动画驱动），快照里的常量不算数。</summary>
    internal static bool Dynamic(JsonObject node) => node.ContainsKey("script") || Animated(node);

    /// <summary>对象的 visible 绑了脚本或动画：快照里是隐藏的也可能在运行时显示。</summary>
    internal bool DynamicVisibility(int id) => Objects[id]["visible"] is JsonObject binding && Dynamic(binding);

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
