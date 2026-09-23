using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 效果前缀的终端捕获点归属校验。渲染器按"终端效果最后一个 LayerNext pass 的写入目标"取帧：
/// 终端效果后面还有这一层的效果（作者效果或渲染器补的合成 pass）时，写入目标是这一层自己的
/// layer composite；终端效果本身就是这一层的最终合成 pass 时，它直接画进场景缓冲（例如 _rt_default），
/// 从那里取到的是整幅场景而不是这一层的纹理。是哪一种由渲染器建图决定，plan 里的静态信息判不出来，
/// 所以只认一次真实探测的结果：捕获点必须出现在同一次运行里这一层自己的效果材质所读的渲染目标之中。
/// </summary>
public static class EffectPrefixCaptureTarget
{
    /// <summary>整幅场景共用的缓冲，任何一层都不能把它当成自己的目标。</summary>
    private static readonly HashSet<string> SceneWideTargets = new(StringComparer.Ordinal)
        { "_rt_default", "_rt_FullFrameBuffer", "_rt_Backbuffer" };

    public const string LayerTargetStatus = "layer_target";
    public const string RejectedStatus = "rejected_not_layer_target";
    public const string ProbeFailedStatus = "probe_failed";

    /// <summary>这一层在运行时证据里自己读写的渲染目标：它的效果材质引用的 _rt_ 纹理，去掉全场景缓冲。</summary>
    public static string[] LayerTargets(JsonObject nativeResult, int ownerLayerId) =>
        (nativeResult["runtime_layers"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(layer => SceneGraph.Int(layer["owner"]) == ownerLayerId)
            .SelectMany(layer => (layer["materials"] as JsonArray ?? []).OfType<JsonObject>())
            .Where(material => Text(material["role"]) == "effect")
            .SelectMany(material => (material["textures"] as JsonArray ?? []).Select(Text))
            .OfType<string>()
            .Where(texture => texture.StartsWith("_rt_", StringComparison.Ordinal) && !SceneWideTargets.Contains(texture))
            .Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// 裁定一次带 trace_scene 的终端捕获探测。返回的 status 为 <see cref="LayerTargetStatus"/> 时可以生成前缀；
    /// 否则 reason 是写进 plan/bake.json 的英文原文，reason_localized 是中英对照。
    /// </summary>
    public static JsonObject Evaluate(JsonObject nativeResult, int ownerLayerId, int terminalEffectId, string? layerName)
    {
        string? target = Text(nativeResult["capture_source"]?["render_target"]);
        string[] layerTargets = LayerTargets(nativeResult, ownerLayerId);
        bool owned = target is not null && !SceneWideTargets.Contains(target) && layerTargets.Contains(target, StringComparer.Ordinal);
        var result = Describe(ownerLayerId, terminalEffectId, layerName, owned ? LayerTargetStatus : RejectedStatus);
        result["render_target"] = target;
        result["capture_pass"] = nativeResult["capture_source"]?["pass"]?.DeepClone();
        result["layer_targets"] = new JsonArray(layerTargets.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray());
        if (!owned)
            new Message("effect_prefix.capture_not_layer_target", [MessageCatalog.EscapeName(layerName), ownerLayerId,
                terminalEffectId, target ?? "(none)", layerTargets.Length == 0 ? "(none)" : string.Join(", ", layerTargets)]).Write(result, "reason");
        return result;
    }

    /// <summary>探测本身没跑成：证明不了捕获点属于这一层，同样不生成前缀。</summary>
    public static JsonObject ProbeFailed(int ownerLayerId, int terminalEffectId, string? layerName, string error)
    {
        var result = Describe(ownerLayerId, terminalEffectId, layerName, ProbeFailedStatus);
        new Message("effect_prefix.capture_probe_failed", [MessageCatalog.EscapeName(layerName), ownerLayerId, terminalEffectId, error]).Write(result, "reason");
        return result;
    }

    /// <summary>作者给图层起的名字，文案点名用；没有就返回 null。</summary>
    public static string? LayerName(JsonObject scene, int ownerLayerId) =>
        Text((scene["objects"] as JsonArray ?? []).OfType<JsonObject>()
            .FirstOrDefault(node => SceneGraph.Int(node["id"]) == ownerLayerId)?["name"]);

    private static JsonObject Describe(int ownerLayerId, int terminalEffectId, string? layerName, string status) => new()
    {
        ["owner_layer_id"] = ownerLayerId, ["layer_name"] = layerName, ["terminal_effect_id"] = terminalEffectId, ["status"] = status
    };

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out string? text) ? text : null;
}
