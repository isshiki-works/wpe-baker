using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

// PLAN §8 PR #48 登记的缺测（C2-transient 消融 A11 漏网）：未解析项的文案与点名图层渲染进 plan。

/// <summary>
/// A11：全幅尾组降级后 plan 采用重算的 loop，它的文案与点名图层（UnresolvedNotes）必须随之换成重算那次的。
/// 场景沿用 FullFrameDemotionChecks（不透明底图 + 音频频谱实时层 + 两个小标签），降级后只烘底图。
/// </summary>
[Trait("Layer", "L1")]
public class TailDemotionNotesTests
{
    private const int Base = FullFrameDemotionChecks.Base, LabelA = FullFrameDemotionChecks.LabelA;

    private static JsonObject Track(int owner, string name, bool looping, double? rate = null)
    {
        var track = new JsonObject { ["source_owner_layer_id"] = owner, ["mechanism"] = "animation", ["track_name"] = name,
            ["duration_seconds"] = 2, ["looping"] = looping, ["event_driven"] = false, ["confidence"] = "high" };
        if (rate is double value) track["playback_rate"] = value;
        return track;
    }

    private static void AssertDemoted(JsonObject plan)
    {
        Assert.Equal("applied", plan["layout_admission_demotion"]!["status"]!.GetValue<string>());
        Assert.Equal([Base], plan["video_groups"]!.AsArray().SelectMany(group => group!["layer_ids"]!.AsArray()).Select(id => id!.GetValue<int>()));
    }

    [Fact]
    public async Task DemotedLoopItemsCarryTheRecomputedKeyedMessage() => await TestTemp.Run(async root =>
    {
        // 整层：标签 A 的轨道不循环排第 0 条，底图的轨道缺播放速率排第 1 条。降级后标签 A 退回实时，只剩底图那条，
        // 它落在第 0 条：沿用整层那份 notes 时同下标对不上，文案就退回 key 为 null 的英文原文。
        JsonObject plan = await FullFrameDemotionChecks.AnalyzeAsync(root, "notes-message", FullFrameDemotionChecks.Scene(), true, new JsonArray(),
            animationPeriods: new JsonArray(Track(LabelA, "label", looping: false), Track(Base, "base", looping: true)));
        AssertDemoted(plan);
        foreach (JsonObject loop in new[] { plan["loop"]!.AsObject(), plan["whole_layer"]!["loop"]!.AsObject() })
        {
            JsonObject item = Assert.IsType<JsonObject>(loop["unresolved"]!.AsArray()[0]);
            JsonObject localized = Assert.IsType<JsonObject>(loop["unresolved_localized"]!.AsArray()[0]);
            Assert.Equal("runtime_animation", item["kind"]!.GetValue<string>());
            Assert.Equal(Base, item["owner_layer_id"]!.GetValue<int>());
            Assert.Equal("unresolved.playback_rate_unresolved", localized["key"]?.GetValue<string>());
            Assert.Equal(Base, localized["owner_layer_id"]!.GetValue<int>());
            Assert.Equal("base", localized["track_name"]!.GetValue<string>());
        }
    });

    [Fact]
    public async Task DemotedStillImageFailureNamesTheRemainingBakedLayer() => await TestTemp.Run(async root =>
    {
        // 整层：标签 A 有一条可证周期的轨道，循环完整、没有未解析项（notes 为空）。降级后只烘底图，没有时间机制，
        // 静止证明卡在底图的材质上并点名它；一行结论里的层名只能从重算那次的 notes 来。
        JsonObject plan = await FullFrameDemotionChecks.AnalyzeAsync(root, "notes-layer", FullFrameDemotionChecks.Scene(), true, new JsonArray(),
            animationPeriods: new JsonArray(Track(LabelA, "label", looping: true, rate: 1)));
        AssertDemoted(plan);
        JsonObject item = Assert.IsType<JsonObject>(plan["loop"]!["unresolved"]!.AsArray()[0]);
        Assert.Equal("source_static", item["kind"]!.GetValue<string>());
        Assert.Equal(Base, item["owner_layer_id"]!.GetValue<int>());
        Assert.Equal("summary.loop_unresolved", plan["summary"]!["key"]!.GetValue<string>());
    });
}
