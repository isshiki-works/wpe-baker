// C2.1c 搬进 Domain 的工作量价值与解码尺寸算术：L0 表（每行一组输入 → 结果）。
using System.Text.Json.Nodes;
using Baker.Core;
using Xunit;

[Trait("Layer", "L0")]
public class DomainCriteriaTests
{
    // 输出尺寸/帧率可比较：乘积有限且三者为正。
    [Theory]
    [InlineData(1920.0, 1080.0, 60.0, true)]
    [InlineData(0.0, 1080.0, 60.0, false)]
    [InlineData(1920.0, -1.0, 60.0, false)]
    [InlineData(double.NaN, 1080.0, 60.0, false)]
    [InlineData(1920.0, 1080.0, double.PositiveInfinity, false)]
    [InlineData(1e200, 1e200, 60.0, false)]
    public void ComparableOutput(double width, double height, double fps, bool expected) =>
        Assert.Equal(expected, WorkloadValue.IsComparableOutput(width, height, fps));

    // 解码量：输出像素数或帧率任一更小即 potential_gain，相等或更大为 not_reduced。
    [Theory]
    [InlineData(1920.0, 1080.0, 60.0, 1920.0, 1080.0, 60.0, "not_reduced")]
    [InlineData(1280.0, 720.0, 60.0, 1920.0, 1080.0, 60.0, "potential_gain")]
    [InlineData(1920.0, 1080.0, 30.0, 1920.0, 1080.0, 60.0, "potential_gain")]
    [InlineData(3840.0, 2160.0, 60.0, 1920.0, 1080.0, 60.0, "not_reduced")]
    [InlineData(1080.0, 1920.0, 60.0, 1920.0, 1080.0, 60.0, "not_reduced")]
    [InlineData(1920.0, 1080.0, 60.0, 1920.0, 1080.0, 59.94, "not_reduced")]
    public void DecodeWork(double width, double height, double fps, double sourceWidth, double sourceHeight, double sourceFps, string expected) =>
        Assert.Equal(expected, WorkloadValue.DecodeWorkStatus(width, height, fps, sourceWidth, sourceHeight, sourceFps));

    // 分量归属：第二段按无符号十进制解析，解析不出来记 −1。
    [Theory]
    [InlineData("video/12/clip", 12)]
    [InlineData("video/012/clip", 12)]
    [InlineData("video/x/clip", -1)]
    [InlineData("video", -1)]
    [InlineData("video/+12/clip", -1)]
    [InlineData("video/ 12/clip", -1)]
    [InlineData("video/99999999999/clip", -1)]
    public void Owner(string component, int expected) => Assert.Equal(expected, WorkloadValue.OwnerOf(component));

    // 铺满居中：占比 ≥ 1 且中心两轴偏差 < 1e-9；plan 里坐标保留 6 位小数，最小非零偏差 1e-6。任一值缺失（NaN）都不算。
    [Theory]
    [InlineData(1.0, 0.5, 0.5, true)]
    [InlineData(0.999999, 0.5, 0.5, false)]
    [InlineData(1.0, 0.500001, 0.5, false)]
    [InlineData(1.0, 0.5, 0.499999, false)]
    [InlineData(double.NaN, 0.5, 0.5, false)]
    [InlineData(1.0, double.NaN, 0.5, false)]
    [InlineData(1.0, 0.5, double.NaN, false)]
    public void FillsCentredCanvas(double fraction, double centreX, double centreY, bool expected) =>
        Assert.Equal(expected, WorkloadValue.FillsCentredCanvas(fraction, centreX, centreY));

    // 静态纹理大于输出：任一边大于即是。
    [Theory]
    [InlineData(3840u, 2160u, 1920.0, 1080.0, true)]
    [InlineData(1920u, 1080u, 1920.0, 1080.0, false)]
    [InlineData(1921u, 100u, 1920.0, 1080.0, true)]
    [InlineData(100u, 1081u, 1920.0, 1080.0, true)]
    public void TextureFootprint(uint width, uint height, double outputWidth, double outputHeight, bool expected) =>
        Assert.Equal(expected, WorkloadValue.TextureExceedsOutput(width, height, outputWidth, outputHeight));

    // 裁剪区一维扩大：对称、起点取偶数、夹在捕获范围内；目标不比原区大或捕获装不下时不动。
    [Theory]
    [InlineData(10, 4, 8, 100, 8, 8)]
    [InlineData(11, 3, 8, 100, 8, 8)]
    [InlineData(0, 4, 8, 100, 0, 8)]
    [InlineData(95, 4, 8, 100, 92, 8)]
    [InlineData(10, 8, 4, 100, 10, 8)]
    [InlineData(10, 4, 200, 100, 10, 4)]
    public void Expand(int start, int size, int target, int capture, int expectedStart, int expectedSize) =>
        Assert.Equal((expectedStart, expectedSize), DecodeDimensions.Expand(start, size, target, capture));

    // 越限项：按宽、高、亮度样本数的顺序；亮度上限为 null 时不查。
    [Theory]
    [InlineData(4097u, 100u, 4096u, 4096u, 0ul, "width")]
    [InlineData(100u, 100u, 4096u, 4096u, 0ul, "")]
    [InlineData(8192u, 4400u, 8192u, 4320u, 33177600ul, "height,luma_samples")]
    [InlineData(8192u, 4400u, 8192u, 4320u, 0ul, "height")]
    [InlineData(8000u, 4320u, 8192u, 4320u, 33177600ul, "luma_samples")]
    public void Violations(uint width, uint height, uint maximumWidth, uint maximumHeight, ulong maximumLuma, string expected) =>
        Assert.Equal(expected, string.Join(",", DecodeDimensions.Violations(width, height, maximumWidth, maximumHeight,
            maximumLuma == 0 ? null : maximumLuma).Select(violation => violation.Measure)));

    // 烘焙价值规则表：按判定顺序，每行只让一条规则成立（不读文件的七条），钉住状态与规则代号。
    [Theory]
    [InlineData(true, true, true, "potential_gain", "video_shell", 2, "unknown", "unresolved_plan")]
    [InlineData(false, true, false, null, "video_shell", 0, "potential_gain", "cached_effect_prefix")]
    [InlineData(false, false, false, "potential_gain", "video_shell", 2, "unknown", "no_working_candidate")]
    [InlineData(false, false, true, "potential_gain", "video_shell", 2, "potential_gain", "reduced_video_decode")]
    [InlineData(false, false, true, "not_reduced", "video_shell", 2, "low_value", "unchanged_video_playback")]
    [InlineData(false, false, true, "not_reduced", "override_accepted", 0, "low_value", "unchanged_video_playback")]
    [InlineData(false, false, true, "not_reduced", "not_video_shell", 2, "potential_gain", "cached_effect_passes")]
    [InlineData(false, false, true, "unknown", "video_shell", 0, "unknown", "needs_work_comparison")]
    public void BakeValueRules(bool blockers, bool prefixes, bool candidates, string? decode, string dominance, int effects, string status, string rule)
    {
        var materials = new JsonArray(new JsonObject { ["role"] = "source", ["shader"] = "genericimage3", ["textures"] = new JsonArray("clip") });
        for (int i = 0; i < effects; ++i) materials.Add(new JsonObject { ["role"] = "effect" });
        var plan = new JsonObject
        {
            ["blockers"] = new JsonArray(),
            ["effect_prefix_caches"] = prefixes ? new JsonArray(new JsonObject()) : new JsonArray(),
            ["loop"] = new JsonObject { ["candidates"] = candidates ? new JsonArray(new JsonObject()) : new JsonArray() },
            ["video_dominant"] = new JsonObject { ["status"] = dominance, ["decode_work"] = decode is null ? null : new JsonObject { ["status"] = decode } },
            ["video_groups"] = new JsonArray(new JsonObject { ["layer_ids"] = new JsonArray(12) }),
        };
        if (blockers) PlanBlockers.Add(plan, new Blocker(BlockerCode.BakeAllocation));
        var runtime = new JsonObject { ["runtime_layers"] = new JsonArray(new JsonObject { ["owner"] = 12, ["has_effect_layer"] = false, ["materials"] = materials }) };
        JsonObject verdict = BakeValueAssessment.Evaluate(plan, runtime, null!, null);
        Assert.Equal((status, rule), (verdict["status"]!.GetValue<string>(), verdict["rule"]!.GetValue<string>()));
    }

    // 按分析请求解析档位（CLI、界面、bake 前重分析共用）：覆盖优先于档位，没选档走旧默认；逐项带来源。
    [Theory]
    [InlineData(null, null, null, 2.0, null, 2.0, 600.0, "default", "default")]
    [InlineData("balanced", null, null, 2.0, 3.0, 3.0, 600.0, "preset", "preset")]
    [InlineData("balanced", 1.5, null, 2.0, 1.5, 1.5, 600.0, "override", "preset")]
    [InlineData("quality", null, 1200.0, 0.0, null, 0.0, 1200.0, "preset", "override")]
    [InlineData(null, 4.0, 60.0, 2.0, 4.0, 4.0, 60.0, "override", "override")]
    public void RetimeProfileFromRequest(string? preset, double? budget, double? loopMaximum, double common,
        double? expectedBudget, double expectedCommon, double expectedMaximum, string budgetSource, string maximumSource)
    {
        var request = new HybridAnalyzeRequest(3, "s", "a", "o", MaximumRetimePercent: common, LoopLengthMaximumSeconds: loopMaximum,
            Preset: preset, RetimeBudgetPercent: budget);
        RetimeProfile profile = RetimeProfileJson.Resolve(request);
        Assert.Equal((preset, expectedBudget, expectedCommon, expectedMaximum, budgetSource, maximumSource),
            (profile.Preset, profile.BudgetPercent, profile.CommonRetimePercent, profile.LoopMaximumSeconds, profile.BudgetSource, profile.LoopMaximumSource));
    }
}
