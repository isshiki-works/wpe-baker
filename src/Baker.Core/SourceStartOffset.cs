using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 源周期路线的起点偏移：原作第 0 帧是孤立的起点异常帧时，录制起点顺延 S = 1 帧，录 [S, S+P)，周期仍是解析值。
/// <para>
/// 为什么会有这种帧：源时间 t = 0 是浮点精确点（g_Time = 0，sin/cos 取到精确极值）。着色器里 pow(0, 0)、除零一类
/// 未定义运算只在这一帧命中（3653991401 图层 17 的 pulse power = 0：sin(−π/2) 恰为 −1 → pulse = 0 → pow(0,0) = NaN → 黑块）；
/// 到第 P 帧，时间与调速常量的 float32 舍入让相位不再精确落在极值上，异常不再出现。所以闭合检验 f[P] 对 f[0] 失败，
/// 而失败完全来自第 0 帧。整周期预热不是这里的答案：它是 analyze 按精灵帧表选的，没有精灵时恒为 0；
/// 这里要的只是不从 t = 0 起录，最小的做法是起点顺延一帧。
/// </para>
/// <para>
/// 判据（全部用闭合检验同一把尺子 D = 64px 瓦片 MAE 最差值，余量同为 8 位取整给的 1.0）：
/// 闭合失败 D(f0, fP) &gt; 1；普通步进 s = max(D(f[P−1], f[P]), D(f1, f2))；
/// 第 0 帧偏离轨迹 D(f0, f1) &gt; s + 1；而第 P 帧（解析周期下它就是正常的"第 0 帧"）接得上第 1 帧 D(fP, f1) ≤ s + 1。
/// 两条合起来就是"换成 f[P] 当第 0 帧一切正常，只有 f[0] 本身不对"。周期真错时 f[P] 接不上 f[1]；
/// 连续多帧异常（f1 也异常）时 f[P] 同样接不上 f[1]；这两种都不偏移，照旧走闭合失败与候选回退。
/// </para>
/// <para>
/// 只在闭合失败时才偏移：闭合通过说明 f[P] 与 f[0] 在 8 位取整内相同，第 0 帧的内容在第 P 帧同样出现，
/// 是渲染器对原作的如实结果；任何整数帧偏移都只会把它挪到片中，不会消掉。
/// 偏移后不另设放行条件：新起点照常过闭合检验与参照式接缝校验，过不了就按原来的候选回退往下走。
/// </para>
/// </summary>
public static class SourceStartOffset
{
    /// <summary>检测到孤立起点异常帧时的起点偏移（帧）。判据保证了第 1 帧正常，所以一帧就够，相位改动最小。</summary>
    public const ulong OffsetFrames = 1;

    public const string AppliedStatus = "applied";
    public const string IsolatedStatus = "isolated_start_anomaly";
    public const string NotIsolatedStatus = "not_isolated";
    public const string ClosedStatus = "closed";
    public const string NotApplicableStatus = "not_applicable";

    /// <summary>
    /// 源周期路线主渲染留下的原帧：第 0、1、2、P−1、P 帧（去重、升序、不超过 P）。
    /// 第 0、P−1、P 帧是闭合检验与接缝参照原有的；第 1、2 帧只给起点异常判据用，不进编码。
    /// </summary>
    public static ulong[] RetainedFrameIndices(ulong loopFrames)
    {
        if (loopFrames == 0) throw new ArgumentException("A loop needs at least one frame.");
        return [.. new[] { 0UL, Math.Min(1UL, loopFrames), Math.Min(2UL, loopFrames), loopFrames - 1, loopFrames }.Distinct().Order()];
    }

    /// <summary>
    /// 起点异常判据。五帧都是渲染器原始 RGBA（第 0、1、2、P−1、P 帧），同尺寸。
    /// 返回记录：status 为 closed（闭合通过，不偏移）、isolated_start_anomaly（偏移）、not_isolated（闭合失败但不是孤立的第 0 帧）
    /// 或 not_applicable（P &lt; 3，五帧不够互不相同）。
    /// </summary>
    public static JsonObject Evaluate(ReadOnlySpan<byte> frame0, ReadOnlySpan<byte> frame1, ReadOnlySpan<byte> frame2,
        ReadOnlySpan<byte> frameBeforeWrap, ReadOnlySpan<byte> frameWrap, int width, int height, bool withAlpha, ulong loopFrames)
    {
        double limit = LoopClosureCheck.MaximumTileMae255;
        var record = new JsonObject
        {
            ["loop_frames"] = loopFrames,
            ["limit_tile_mae_255"] = limit,
            ["tile_size"] = LoopClosureCheck.TileSize,
            ["frame_source"] = "renderer_rgba_before_encoding"
        };
        if (loopFrames < 3)
        {
            record["status"] = NotApplicableStatus;
            return record;
        }
        double D(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => LoopClosureCheck.WorstTileMae(a, b, width, height, withAlpha);
        double closure = D(frame0, frameWrap);
        record["closure_f0_fp"] = Math.Round(closure, 4);
        if (closure <= limit)
        {
            record["status"] = ClosedStatus;
            return record;
        }
        double wrapStep = D(frameBeforeWrap, frameWrap), nextStep = D(frame1, frame2);
        double ordinary = Math.Max(wrapStep, nextStep);
        double detour = D(frame0, frame1), bridge = D(frameWrap, frame1);
        bool isolated = detour > ordinary + limit && bridge <= ordinary + limit;
        record["step_fp1_fp"] = Math.Round(wrapStep, 4);
        record["step_f1_f2"] = Math.Round(nextStep, 4);
        record["ordinary_step"] = Math.Round(ordinary, 4);
        record["step_f0_f1"] = Math.Round(detour, 4);
        record["bridge_fp_f1"] = Math.Round(bridge, 4);
        record["status"] = isolated ? IsolatedStatus : NotIsolatedStatus;
        record["basis"] = "Frame P is the analytic period's frame 0. Frame 0 is an isolated start anomaly when f[0] leaves the trajectory " +
            "(D(f0,f1) > ordinary step + 1) while f[P] continues into f[1] like an ordinary step (D(fP,f1) <= ordinary step + 1); " +
            "ordinary step = max(D(f[P-1],f[P]), D(f1,f2)); D = worst 64px tile MAE, the closure check's own measure and 8-bit allowance.";
        return record;
    }

    /// <summary>判据记录是否要求偏移。</summary>
    public static bool RequiresOffset(JsonObject? evaluation) => evaluation?["status"]?.GetValue<string>() == IsolatedStatus;

    /// <summary>写进 bake.json 的偏移记录。</summary>
    public static JsonObject Applied(string groupId, JsonObject evaluation, ulong startFrame) => new()
    {
        ["status"] = AppliedStatus,
        ["start_frame"] = startFrame,
        ["group_id"] = groupId,
        ["evaluation_at_origin"] = evaluation.DeepClone(),
        ["basis"] = "Source frame 0 was an isolated anomaly (the closure check failed only because of it), so the capture starts one frame later " +
            "and records [S, S+P). The period is still analytic; the new start is judged by the same closure and encoded seam checks."
    };

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
