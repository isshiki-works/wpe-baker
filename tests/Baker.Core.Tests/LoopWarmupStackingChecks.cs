using System.Text.Json.Nodes;
using Baker.Core;

/// <summary>
/// 精灵 float32 整周期预热（source_period_warmup_frames，0 或 P）与平稳随机粒子预热 W 都是录制前多渲的帧，
/// 合并后 master 跳过的帧数必须是两者之和再加残差路线起点相位 S；起点搜索用不含 S 的同一基准。
/// </summary>
internal static class LoopWarmupStackingChecks
{
    internal static void Run(Action<bool, string> check)
    {
        // 源周期路线：没有粒子预热、起点 0，只剩精灵预热（0 或 P）。
        check(LoopWarmup.MasterFrames(0, 336, 0, 0) == 0 &&
              LoopWarmup.MasterFrames(336, 336, 0, 0) == 336,
            "warmup stacking: source-period route keeps sprite warmup 0 or P unchanged");

        // 残差路线没有精灵预热：与粒子分支相同，W + S（Far From Home 实测 240 + 568 = 808）。
        check(LoopWarmup.MasterFrames(0, 1370, 240, 568) == 808,
            "warmup stacking: residual route without sprite warmup is particle warmup plus start phase");

        // 两种预热同时存在：取和，不是取大，也不是互相覆盖。
        ulong stacked = LoopWarmup.MasterFrames(288, 288, 240, 96);
        check(stacked == 288 + 240 + 96 && stacked != Math.Max(288UL, 240UL) + 96 && stacked != 240 + 96 && stacked != 288 + 96,
            "warmup stacking: sprite warmup and particle warmup are summed before the start phase");

        // 起点搜索基准不含 S，master = 基准 + S：搜索样本第 s 帧就是 master 以 s 为起点时的第 0 帧。
        ulong searchBase = LoopWarmup.BaseFrames(288, 288, 240);
        check(searchBase == 528 && LoopWarmup.MasterFrames(288, 288, 240, 96) == searchBase + 96,
            "warmup stacking: start search and master share the sprite plus particle warmup base");

        // 精灵预热只能是 0 或 P；不是整周期会改周期分量相位，直接拒绝。
        bool rejected;
        try { LoopWarmup.MasterFrames(100, 288, 240, 0); rejected = false; }
        catch (InvalidDataException) { rejected = true; }
        check(rejected, "warmup stacking: sprite warmup that is not exactly one period is rejected even with particle warmup");

        bool overflow;
        try { LoopWarmup.MasterFrames(ulong.MaxValue, ulong.MaxValue, 1, 0); overflow = false; }
        catch (OverflowException) { overflow = true; }
        check(overflow, "warmup stacking: the sum is checked for overflow");

        // 候选表读法：首选候选带字段时读它，没有字段或候选表为空时为 0。
        var withWarmup = new JsonArray(new JsonObject { ["frames"] = 336, ["source_period_warmup_frames"] = 336UL },
            new JsonObject { ["frames"] = 672 });
        var withoutWarmup = new JsonArray(new JsonObject { ["frames"] = 672 },
            new JsonObject { ["frames"] = 336, ["source_period_warmup_frames"] = 336UL });
        check(LoopWarmupJson.CandidateSourcePeriodWarmupFrames(withWarmup) == 336 &&
              LoopWarmupJson.CandidateSourcePeriodWarmupFrames(withoutWarmup) == 0 &&
              LoopWarmupJson.CandidateSourcePeriodWarmupFrames(new JsonArray()) == 0,
            "warmup stacking: sprite warmup is read from the first candidate only");
    }
}
