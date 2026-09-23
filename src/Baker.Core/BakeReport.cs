using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// bake.json（schema 2）拒绝出口的报告：固定头部（身份、源哈希、plan、效果分辨率设置）+ 本出口的证据字段 +
/// 固定尾部（frames=0、空 groups、loop_validation、两项 not_verified）+ 尾随字段。<see cref="ToJson"/> 按这个顺序写出，
/// 与改动前四处手写骨架逐字节相同；身份头与占位取自 <see cref="BakeReportWriter"/>。证据与尾随字段按调用方插入顺序搬进报告（节点移动，不复制）。
/// </summary>
internal sealed record BakeRejection(string Status, string SourceSha256, JsonObject Plan, double EffectRenderScale,
    bool MatchEffectResolution, string LoopValidation, JsonObject Evidence, JsonObject Trailer)
{
    public JsonObject ToJson()
    {
        JsonObject json = BakeReportWriter.Head("hybrid_video_candidate", Status, SourceSha256);
        json["plan"] = Plan;
        json["effect_render_scale"] = EffectRenderScale;
        json["match_effect_resolution"] = MatchEffectResolution;
        Move(Evidence, json);
        json["frames"] = 0;
        json["groups"] = new JsonArray();
        json["loop_validation"] = LoopValidation;
        BakeReportWriter.AddNotVerified(json);
        Move(Trailer, json);
        return json;
    }

    private static void Move(JsonObject from, JsonObject to)
    {
        foreach (var (name, value) in from.ToArray()) { from.Remove(name); to[name] = value; }
    }
}
