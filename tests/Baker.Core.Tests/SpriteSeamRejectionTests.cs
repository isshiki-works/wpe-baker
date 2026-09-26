using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

// PLAN §8 C2.2c 登记的缺口：精灵 float32 接缝在候选上两个起点都对不齐时，LoopAnalysis 丢掉该候选并记一条带被拒数的未解析项。
[Trait("Layer", "L1")]
public class SpriteSeamRejectionTests
{
    // 三帧 0.03/0.12/0.15：float32 下首帧偏短、后两帧偏长，总和偏长。60 fps 下十进制周期 0.3 s = 18 帧。
    // 起点 0 与整周期预热 18 帧两个起点都逐帧对不齐（均匀帧表只在 t = 0 有奇点，预热总能闭合；这张表的内部边界偏向与总漂移相反）。
    // 36 帧起的候选在预热后闭合，所以要全拒就把循环上限压到 0.5 s，只留 18 帧一个候选。
    private static readonly float[] Table = [0.03f, 0.12f, 0.15f];

    [Fact]
    public async Task EveryCandidateMissingTheSeamFromBothStartsIsDroppedAndCounted() => await TestTemp.Run(root =>
    {
        Assert.Equal(new SpriteSeamPhase.Selection(null, SpriteSeamPhase.Verdict.Mismatch, SpriteSeamPhase.Verdict.Mismatch),
            SpriteSeamPhase.Select([Table], 60, 1, 18));
        Assert.Equal(new SpriteSeamPhase.Selection(36, SpriteSeamPhase.Verdict.Mismatch, SpriteSeamPhase.Verdict.Closed),
            SpriteSeamPhase.Select([Table], 60, 1, 36));

        string directory = Path.Combine(root, "sprite-seam-rejected");
        Directory.CreateDirectory(Path.Combine(directory, "materials"));
        File.WriteAllText(Path.Combine(directory, "project.json"), "{\"type\":\"scene\",\"file\":\"scene.json\"}");
        File.WriteAllText(Path.Combine(directory, "scene.json"), "{\"objects\":[]}");
        File.WriteAllBytes(Path.Combine(directory, "materials", "Drift.tex"), SpriteSeamPhaseChecks.SpriteTex(Table));
        using var source = new ProjectSource(directory);
        JsonObject Analyze(double ceilingSeconds) => LoopAnalysis.Analyze(
            new JsonObject { ["objects"] = new JsonArray { new JsonObject { ["id"] = 1 } } }, source, null,
            new JsonObject { ["runtime_animation_periods"] = new JsonArray { new JsonObject {
                ["source_owner_layer_id"] = 1, ["mechanism"] = "sprite", ["track_name"] = "Drift", ["duration_seconds"] = 0.3,
                ["looping"] = true, ["event_driven"] = false, ["confidence"] = "high" } } },
            [1], 60, 1, 2, CommonLoopPreference.Balanced, ceilingSeconds, null).ToJson();

        // 全拒：唯一的候选被丢掉，求解器本身有解所以没有 no_candidate_reason，未解析项只有这一条、被拒数 1。
        JsonObject rejected = Analyze(0.5);
        JsonObject item = Assert.IsType<JsonObject>(Assert.Single(rejected["unresolved"]!.AsArray()));
        Assert.Empty(rejected["candidates"]!.AsArray());
        Assert.Equal("no_analytic_candidate", rejected["status"]!.GetValue<string>());
        Assert.Null(rejected["no_candidate_reason"]);
        Assert.Equal("sprite_float32_seam", item["kind"]!.GetValue<string>());
        Assert.Equal(1, item["rejected_candidate_count"]!.GetValue<int>());

        // 对照：上限放到 0.7 s 多出 36 帧候选，它带整周期预热留下；被拒的仍只算 18 帧那一个。
        JsonObject partly = Analyze(0.7);
        JsonObject kept = Assert.IsType<JsonObject>(Assert.Single(partly["candidates"]!.AsArray()));
        Assert.Equal(36UL, kept["frames"]!.GetValue<ulong>());
        Assert.Equal(36UL, kept["source_period_warmup_frames"]!.GetValue<ulong>());
        JsonObject partlyItem = Assert.IsType<JsonObject>(Assert.Single(partly["unresolved"]!.AsArray()));
        Assert.Equal("sprite_float32_seam", partlyItem["kind"]!.GetValue<string>());
        Assert.Equal(1, partlyItem["rejected_candidate_count"]!.GetValue<int>());
    });
}
