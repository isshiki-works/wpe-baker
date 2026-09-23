using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 旧成品里的源周期起点偏移记录（source_start_offset）：早期版本在原作第 0 帧是孤立的起点异常帧时把录制起点顺延 1 帧。
/// 现在的烘焙只渲一次、不自动重渲，不再产生这条记录；这里只剩读已有 bake.json 时的放行判定。
/// </summary>
public static class SourceStartOffset
{
    /// <summary>旧记录里唯一合法的偏移（帧）。</summary>
    public const ulong OffsetFrames = 1;

    public const string AppliedStatus = "applied";

    /// <summary>
    /// 保存的源周期结果能不能带非零起点：只有偏移记录是 applied、起点等于 <see cref="OffsetFrames"/>，
    /// 并且与 source_start_frame 一致时才行。残差路线的非零起点另有规则，不走这里。
    /// </summary>
    public static bool Allows(JsonObject result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result["seam_policy"]?.GetValue<string>() == "source_period_no_repair" &&
            result["source_start_offset"] is JsonObject offset &&
            offset["status"]?.GetValue<string>() == AppliedStatus &&
            offset["start_frame"] is JsonValue start && start.TryGetValue(out ulong startFrame) && startFrame == OffsetFrames &&
            result["source_start_frame"] is JsonValue reported && reported.TryGetValue(out ulong reportedFrame) && reportedFrame == startFrame;
    }
}
