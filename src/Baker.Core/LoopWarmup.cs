using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 从 plan 候选表读精灵整周期预热帧数。预热算术在 Domain 的 <see cref="LoopWarmup"/>；这里用静态扩展挂回 LoopWarmup 名下，
/// 现有调用方写法不变（登记在转发器清单，C3 改为直接调用）。
/// </summary>
public static class LoopWarmupJson
{
    extension(LoopWarmup)
    {
        /// <summary>当前首选候选（candidates[0]）上 analyze 写的精灵整周期预热帧数；没有这个字段或候选表为空时为 0。</summary>
        public static ulong CandidateSourcePeriodWarmupFrames(JsonArray candidates) =>
            candidates.Count > 0 ? candidates[0]?["source_period_warmup_frames"]?.GetValue<ulong>() ?? 0 : 0;
    }
}
