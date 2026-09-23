using System.Text;
using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

// PLAN §8 PR #48 登记的两处缺测（C2-transient 消融 A10、A11 漏网）：未解析项的文案与点名图层渲染进 plan。

/// <summary>
/// A10：特效前缀硬解预检被拒的未解析项。条目只写 v3 字段，文案 {key, params} 由预检写进同下标的 unresolved_localized，
/// PlanNarrative.Attach 沿用它（Attach 不带 notes 时，没有它就退回 key 为 null 的英文原文）。
/// </summary>
[Trait("Layer", "L1")]
public class EffectPrefixPreflightNotesTests
{
    [Fact]
    public async Task RejectedPreflightItemsKeepTheirKeyedMessageNamingTheLayer() => await TestTemp.Run(root =>
    {
        string directory = Path.Combine(root, "preflight-source");
        Directory.CreateDirectory(Path.Combine(directory, "materials"));
        Directory.CreateDirectory(Path.Combine(directory, "models"));
        File.WriteAllText(Path.Combine(directory, "project.json"), "{\"type\":\"scene\",\"file\":\"scene.json\"}");
        File.WriteAllText(Path.Combine(directory, "scene.json"), "{\"objects\":[]}");
        // 三个前缀缓存：9000×100 不透明也超 HEVC 8192 宽；5108×3160 只在透明左右并排（10216 宽）时超；640×360 通过。
        var caches = new JsonArray();
        foreach ((int owner, uint width, uint height) in new[] { (21, 9000u, 100u), (22, 5108u, 3160u), (23, 640u, 360u) })
        {
            File.WriteAllText(Path.Combine(directory, "models", $"L{owner}.json"), $"{{\"material\":\"materials/L{owner}.json\"}}");
            File.WriteAllText(Path.Combine(directory, "materials", $"L{owner}.json"), $"{{\"passes\":[{{\"textures\":[\"source{owner}\"]}}]}}");
            File.WriteAllBytes(Path.Combine(directory, "materials", $"source{owner}.tex"), TexPreamble(width, height));
            caches.Add(new JsonObject { ["owner_layer_id"] = owner, ["source_image"] = $"models/L{owner}.json" });
        }
        var plan = new JsonObject {
            ["settings"] = new JsonObject { ["fps_numerator"] = 60, ["fps_denominator"] = 1 },
            ["layers"] = new JsonArray(new JsonObject { ["id"] = 21, ["name"] = "Wide strip" }, new JsonObject { ["id"] = 22, ["name"] = "Atlas" },
                new JsonObject { ["id"] = 23, ["name"] = "Badge" }),
            ["blockers"] = new JsonArray(),
            ["loop"] = new JsonObject { ["candidates"] = new JsonArray(), ["unresolved"] = new JsonArray() },
            ["effect_prefix_caches"] = caches };
        using var source = new ProjectSource(directory);
        var request = new HybridAnalyzeRequest(2, directory, root, Path.Combine(root, "out"), 9000, 3200, 60, 1);
        plan["effect_prefix_hardware_decode_preflight"] = HardwareDecodeDimensions.PredictEffectPrefixCaches(plan, source, null, request, new JsonObject());
        PlanNarrative.Attach(plan);

        JsonObject[] entries = plan["effect_prefix_hardware_decode_preflight"]!.AsArray().OfType<JsonObject>().ToArray();
        Assert.Equal([21, 22, 23], entries.Select(entry => entry["owner_layer_id"]!.GetValue<int>()));
        Assert.Equal(["predicted_rejected", "predicted_rejected_if_transparent", "predicted_pass"],
            entries.Select(entry => entry["status"]!.GetValue<string>()));

        (JsonObject Item, JsonObject Localized) Single(JsonObject entry)
        {
            JsonObject item = Assert.IsType<JsonObject>(Assert.Single(entry["unresolved"]!.AsArray()));
            JsonObject localized = Assert.IsType<JsonObject>(Assert.Single(entry["unresolved_localized"]!.AsArray()));
            Assert.Equal("hardware_decode_dimensions", item["kind"]!.GetValue<string>());
            Assert.Equal(item["kind"]!.GetValue<string>(), localized["kind"]!.GetValue<string>());
            Assert.Equal(item["owner_layer_id"]!.GetValue<int>(), localized["owner_layer_id"]!.GetValue<int>());
            return (item, localized);
        }
        string[] Params(JsonObject localized) => localized["params"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();

        // 参数 0 是点名的图层（L 编号 + 层名），1 是源纹理尺寸，2 是被拒的编码尺寸。
        var (wide, wideLocalized) = Single(entries[0]);
        Assert.Equal(21, wide["owner_layer_id"]!.GetValue<int>());
        Assert.Equal("unresolved.hardware_decode_dimensions_predicted", wideLocalized["key"]!.GetValue<string>());
        Assert.Equal(["L21 \"Wide strip\"", "9000×100", "9000×144"], Params(wideLocalized)[..3]);

        var (atlas, atlasLocalized) = Single(entries[1]);
        Assert.Equal(22, atlas["owner_layer_id"]!.GetValue<int>());
        Assert.Equal("unresolved.hardware_decode_dimensions_if_transparent", atlasLocalized["key"]!.GetValue<string>());
        Assert.Equal(["L22 \"Atlas\"", "5108×3160", "10216×3160"], Params(atlasLocalized)[..3]);

        Assert.Empty(entries[2]["unresolved"]!.AsArray());
        Assert.Empty(entries[2]["unresolved_localized"]!.AsArray());
    });

    // 与 HardwareDecodeDimensionsChecks 的 TEX 头同一布局：TEXV0005/TEXI0001、格式、标志、纹理尺寸、图像尺寸。
    private static byte[] TexPreamble(uint width, uint height)
    {
        byte[] preamble = new byte[64];
        Encoding.ASCII.GetBytes("TEXV0005\0TEXI0001\0").CopyTo(preamble, 0);
        BitConverter.GetBytes(0).CopyTo(preamble, 18); BitConverter.GetBytes(2u).CopyTo(preamble, 22);
        BitConverter.GetBytes(16384u).CopyTo(preamble, 26); BitConverter.GetBytes(4096u).CopyTo(preamble, 30);
        BitConverter.GetBytes(width).CopyTo(preamble, 34); BitConverter.GetBytes(height).CopyTo(preamble, 38);
        return preamble;
    }
}

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
        Assert.Contains("\"Base plate\"", plan["summary"]!["zh"]!.GetValue<string>(), StringComparison.Ordinal);
    });
}
